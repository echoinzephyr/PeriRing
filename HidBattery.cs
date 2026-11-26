using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace BatteryDisplay;

// MCHOSE status query only (0x06). No configuration commands are sent.
// Protocol: https://github.com/alexfrih/mchose-linux/blob/main/PROTOCOL.md
internal static class HidBattery
{
    [StructLayout(LayoutKind.Sequential)]
    private struct InterfaceInfo
    {
        public uint Size;
        public Guid ClassGuid;
        public uint Flags;
        public UIntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Attributes
    {
        public uint Size;
        public ushort Vendor, Product, Version;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Caps
    {
        public ushort Usage, UsagePage, InputLength, OutputLength, FeatureLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort LinkNodes, InputButtons, InputValues, InputData, OutputButtons, OutputValues, OutputData, FeatureButtons, FeatureValues, FeatureData;
    }

    [DllImport("hid.dll")] private static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_GetAttributes(SafeFileHandle file, ref Attributes attributes);
    [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_GetPreparsedData(SafeFileHandle file, out IntPtr data);
    [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_FreePreparsedData(IntPtr data);
    [DllImport("hid.dll")] private static extern int HidP_GetCaps(IntPtr data, out Caps caps);
    [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_GetProductString(SafeFileHandle file, byte[] buffer, uint length);
    [DllImport("hid.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.U1)]
    internal static extern bool HidD_SetFeature(SafeFileHandle file, byte[] report, int length);
    [DllImport("hid.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.U1)]
    internal static extern bool HidD_GetFeature(SafeFileHandle file, byte[] report, int length);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(ref Guid guid, string? enumerator, IntPtr hwnd, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr info, ref Guid guid, uint index, ref InterfaceInfo data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr set, ref InterfaceInfo data, IntPtr detail, uint size, out uint required, IntPtr info);
    [DllImport("setupapi.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    private static IEnumerable<string> Paths()
    {
        HidD_GetHidGuid(out var guid);
        var set = SetupDiGetClassDevsW(ref guid, null, IntPtr.Zero, 0x12);
        if (set == new IntPtr(-1)) yield break;
        try
        {
            for (uint i = 0; ; i++)
            {
                var data = new InterfaceInfo { Size = (uint)Marshal.SizeOf<InterfaceInfo>() };
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref data)) yield break;
                SetupDiGetDeviceInterfaceDetailW(set, ref data, IntPtr.Zero, 0, out uint required, IntPtr.Zero);
                if (required < 8) continue;
                var detail = Marshal.AllocHGlobal((int)required);
                string? path = null;
                try
                {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (SetupDiGetDeviceInterfaceDetailW(set, ref data, detail, required, out _, IntPtr.Zero))
                        path = Marshal.PtrToStringUni(detail + 4);
                }
                finally { Marshal.FreeHGlobal(detail); }
                if (path is not null) yield return path;
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }

    internal static List<HidDescriptor> Enumerate(CancellationToken token)
    {
        var result = new List<HidDescriptor>();
        foreach (string path in Paths())
        {
            token.ThrowIfCancellationRequested();
            using var handle = CreateFileW(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (handle.IsInvalid) continue;
            var attributes = new Attributes { Size = (uint)Marshal.SizeOf<Attributes>() };
            if (!HidD_GetAttributes(handle, ref attributes)) continue;
            if (!HidD_GetPreparsedData(handle, out var data)) continue;
            Caps caps;
            try { if (HidP_GetCaps(data, out caps) < 0) continue; }
            finally { HidD_FreePreparsedData(data); }
            var name = new byte[256];
            string product = HidD_GetProductString(handle, name, (uint)name.Length) ? Encoding.Unicode.GetString(name).TrimEnd('\0') : "USB 外设";
            result.Add(new(path, attributes.Vendor, attributes.Product, product, caps));
        }
        return result;
    }

    internal static Peripheral? ParseStatus(byte[] response, string product, string path, ushort? wiredProduct = null, byte reportId = 0x11)
    {
        if (response.Length < 13 || response[0] != reportId || response[1] != 0xF9) return null;
        byte[] payload = response.Skip(2).Select(b => (byte)(b ^ 0xFF)).ToArray();
        int vendor = payload[0] | payload[1] << 8;
        int model = payload[2] | payload[3] << 8;
        if (vendor is not (0x5253 or 0x3837) || model == 0 || payload[9] > 100) return null;
        // Direct devices identify their own USB model while the wireless link bit can be clear.
        bool wired = wiredProduct.HasValue;
        if (wired ? model != wiredProduct : (payload[8] & 0x08) == 0) return null;
        string name = model switch { 0x31 => "迈从 M7 Ultra", 0xB0 => "迈从 L7 Pro", _ => $"迈从 · {product}" };
        return new("hid:" + path.ToUpperInvariant(), name, true, payload[9], wired ? "迈从 USB 有线" : "迈从接收器", wired ? "USB" : "2.4G", payload[10] == 1,
            VendorDisplay: VendorBatteryDisplay(payload[9]));
    }
    // Official M HUB display bins: 0..14 -> 10, 15..24 -> 20, ... 75..84 -> 80, 95..100 -> 100.
    // https://www.mchose.com.cn/assets/index-De3mthp9.js (verified 2026-11-14)
    internal static int VendorBatteryDisplay(int raw) => Math.Clamp((raw + 5) / 10 * 10, 10, 100);
}
