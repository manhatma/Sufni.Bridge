using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Svg.Skia;
using SkiaSharp;
using Svg.Skia;

namespace Sufni.Bridge.Services;

internal static class SkiaPdfWriter
{
    internal static string WritePdf(
        string pdfPath,
        IReadOnlyList<string> svgXml,
        Action<SKDocument, IReadOnlyList<SKSvg>> draw)
    {
        // SVG parsing performs expensive XML processing and Skia picture recording. Preserve
        // input order because callers use the picture index to retain their page metadata.
        var svgObjects = svgXml
            .AsParallel()
            .AsOrdered()
            .Select(xml =>
            {
                var svg = new SKSvg();
                svg.FromSvg(xml);
                return svg;
            })
            .ToList();

        try
        {
            using var stream = new FileStream(pdfPath, FileMode.Create);
            using var document = SKDocument.CreatePdf(stream);

            draw(document, svgObjects);

            document.Close();
        }
        finally
        {
            foreach (var svg in svgObjects)
                svg.Dispose();
        }

        return pdfPath;
    }
}
