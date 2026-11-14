# 环电 · PeriRing 图标

名称含义：用分段圆环呈现多个外设的电量。

最终文件：`periring.png` 为原始图标，`periring.ico` 含 16、20、24、32、40、48、64、128、256 像素版本。运行中的托盘使用程序实时绘制的同源圆环与菱形，圆弧长度随电量改变。

制作方式：内置图像生成工具生成并修整；`tools/Export-AppIcon.ps1` 仅负责标准尺寸转换和 ICO 封装。

初始提示词：

Use case: logo-brand. Asset type: finished Windows application icon for a minimal peripheral battery tray utility called PeriRing (Chinese name 环电). Create exactly ONE icon, centered on a square 1024 x 1024 transparent canvas. Subject: a beautifully proportioned mint-green circular ring divided into three thick equal curved segments with small clean gaps and rounded ends, surrounding a small hollow diamond at the exact center. The ring symbolizes multiple device batteries; the diamond is its distinctive signature. Geometry is simple and exceptionally crisp, silhouette readable at 16–32px. Occupy about 80 percent of the canvas with equal margins. Style: premium modern Fluent-inspired app mark, almost flat, restrained soft mint-to-teal shading inside the strokes only, consistent bold stroke weight. A polished real software icon, not an illustration. Palette: pale mint #A2E8C1, seafoam #69CEA5, teal #38A991. The center diamond uses pale mint, transparent inside. Background fully transparent, including gaps and the central hole. No square tile or badge background, no text, no letters, no lightning bolt, no battery pictogram, no glow outside the mark, no shadow, no mockup, no presentation sheet, no extra symbols.

最终修整提示词：

Refine this into one finished square Windows app icon. Preserve the centered three-segment mint circular ring and hollow mint diamond, their exact arrangement and smooth shading. Put the mark on a perfectly uniform opaque deep charcoal #182820 background covering the whole square canvas. The entire spaces between ring and diamond, the center hole, and the gaps must be the exact same clean charcoal as the background. Completely remove ALL green/cyan flecks, noise, grain, edge halos, white artifacts and transparency artifacts. Crisp smooth antialiased geometric edges, consistent gap sizes. Three equal arc segments with rounded ends. Centered mark occupies 80% canvas with equal margins. No text, no border, no shadows, no glow, no added symbols. This is the actual finished application icon, not a mockup or sheet.
