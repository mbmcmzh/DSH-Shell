using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Text.RegularExpressions;
using Svg;

// 用法：IconRender <favicon.svg> <输出目录>
// 把官方鲸鱼 SVG 渲染为品牌蓝，直接输出多尺寸 icon.ico + icon-256.png（预览用）。
if (args.Length < 2)
{
    Console.Error.WriteLine("usage: IconRender <favicon.svg> <outDir>");
    return 1;
}

var svgPath = args[0];
var outDir = args[1];

var svgText0 = File.ReadAllText(svgPath);
// 去掉 <style>（含 @media 查询，Svg 库不支持）。
var svgText = Regex.Replace(svgText0, "<style[\\s\\S]*?</style>", "", RegexOptions.IgnoreCase);
// 原 path 自带 fill="#000" 与 fill-opacity：先清掉，再注入品牌蓝。
svgText = Regex.Replace(svgText, "(<path\\b[^>]*?)\\s+fill=\"[^\"]*\"", "$1", RegexOptions.IgnoreCase);
svgText = Regex.Replace(svgText, "(<path\\b[^>]*?)\\s+fill-opacity=\"[^\"]*\"", "$1", RegexOptions.IgnoreCase);
svgText = Regex.Replace(svgText, "<path\\b", "<path fill=\"#4D6BFE\"", RegexOptions.IgnoreCase);

var doc = SvgDocument.FromSvg<SvgDocument>(svgText);

int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };
var pngs = new List<byte[]>();

foreach (var size in sizes)
{
    doc.Width = size;
    doc.Height = size;

    using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
    using (var g = Graphics.FromImage(bmp))
    {
        g.SmoothingMode = SmoothingMode.HighQuality;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);
        doc.Draw(g);
    }

    using var ms = new MemoryStream();
    bmp.Save(ms, ImageFormat.Png);
    var png = ms.ToArray();
    pngs.Add(png);

    if (size == 256)
        File.WriteAllBytes(Path.Combine(outDir, "icon-256.png"), png);
}

// 组装 ICO（Vista+ PNG 内嵌格式）
var icoPath = Path.Combine(outDir, "icon.ico");
using (var fs = File.Create(icoPath))
using (var bw = new BinaryWriter(fs))
{
    bw.Write((ushort)0);
    bw.Write((ushort)1);
    bw.Write((ushort)pngs.Count);
    var offset = 6 + 16 * pngs.Count;
    for (var i = 0; i < pngs.Count; i++)
    {
        var s = sizes[i];
        bw.Write((byte)(s >= 256 ? 0 : s));
        bw.Write((byte)(s >= 256 ? 0 : s));
        bw.Write((byte)0);
        bw.Write((byte)0);
        bw.Write((ushort)1);
        bw.Write((ushort)32);
        bw.Write((uint)pngs[i].Length);
        bw.Write((uint)offset);
        offset += pngs[i].Length;
    }
    foreach (var png in pngs)
        bw.Write(png);
}

Console.WriteLine($"OK: {icoPath} ({new FileInfo(icoPath).Length} bytes)");
return 0;
