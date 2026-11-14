using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace BatteryDisplay;

internal sealed record NativeReading(string InstanceId, Guid Container, string Address, string Name, int Battery);

internal static partial class NativeBattery
{
    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInfo
    {
        public uint Size;
        public Guid ClassGuid;
        public uint DevInst;
        public UIntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey(Guid guid, uint id)
    {
        public Guid Guid = guid;
        public uint Id = id;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(IntPtr guid, string? enumerator, IntPtr hwnd, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref DeviceInfo info);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInstanceIdW(IntPtr set, ref DeviceInfo info, StringBuilder id, uint size, out uint required);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDevicePropertyW(IntPtr set, ref DeviceInfo info, ref PropertyKey key, out uint type, byte[] buffer, uint size, out uint required, uint flags);
    [DllImport("setupapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    private static byte[]? Read(IntPtr set, ref DeviceInfo info, Guid guid, uint pid)
    {
        var key = new PropertyKey(guid, pid);
        var buffer = new byte[2048];
        return SetupDiGetDevicePropertyW(set, ref info, ref key, out _, buffer, (uint)buffer.Length, out uint length, 0)
            ? buffer[..(int)length] : null;
    }

    public static List<NativeReading> ReadAll()
    {
        var results = new List<NativeReading>();
        var set = SetupDiGetClassDevsW(IntPtr.Zero, null, IntPtr.Zero, 6); // Present, all classes.
        if (set == new IntPtr(-1)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            for (uint i = 0; ; i++)
            {
                var info = new DeviceInfo { Size = (uint)Marshal.SizeOf<DeviceInfo>() };
                if (!SetupDiEnumDeviceInfo(set, i, ref info))
                {
                    if (Marshal.GetLastWin32Error() != 259) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                    break;
                }
                var id = new StringBuilder(1024);
                if (!SetupDiGetDeviceInstanceIdW(set, ref info, id, (uint)id.Capacity, out _)) continue;
                string instance = id.ToString();
                if (!instance.StartsWith("BTH", StringComparison.OrdinalIgnoreCase)) continue;
                var battery = Read(set, ref info, new("104ea319-6ee2-4701-bd47-8ddbf425bbe5"), 2);
                if (battery is not { Length: > 0 } || battery[0] > 100) continue;
                var containerBytes = Read(set, ref info, new("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c"), 2);
                Guid container = containerBytes is { Length: 16 } ? new(containerBytes) : Guid.Empty;
                var nameBytes = Read(set, ref info, new("a45c254e-df1c-4efd-8020-67d146a850e0"), 14)
                    ?? Read(set, ref info, new("a45c254e-df1c-4efd-8020-67d146a850e0"), 2);
                string name = nameBytes is null ? instance : Encoding.Unicode.GetString(nameBytes).TrimEnd('\0');
                var addressBytes = Read(set, ref info, new("2bd67d8b-8beb-48d5-87e0-6cda3428040a"), 1);
                string address = addressBytes is null ? "" : NormalizeAddress(Encoding.Unicode.GetString(addressBytes).TrimEnd('\0'));
                if (address.Length != 12)
                {
                    var match = AddressPattern().Match(instance);
                    address = match.Success ? match.Groups[1].Value.ToUpperInvariant() : "";
                }
                results.Add(new(instance, container, address, name, battery[0]));
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return results;
    }

    public static string NormalizeAddress(string address) => new(address.Where(Uri.IsHexDigit).Select(char.ToUpperInvariant).ToArray());
    [GeneratedRegex(@"(?:DEV_|&)([0-9A-Fa-f]{12})(?:\\|_|&|$)")]
    private static partial Regex AddressPattern();
}
