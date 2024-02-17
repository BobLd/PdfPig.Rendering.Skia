using System.Collections.Generic;
using System.IO;
using System.Linq;
using UglyToad.PdfPig.Graphics.Colors;
using Xunit;

namespace UglyToad.PdfPig.Rendering.Skia.Tests
{
    public class VisualTests
    {
        private const int _scale = 2;
        private const string _outputPath = "Output";

        public static IEnumerable<object[]> GetAllDocuments => Directory.EnumerateFiles("Documents", "*.pdf")
            .Select(x => new object[]
            {
                Path.GetFileName(x)
            });

        public VisualTests()
        {
            Directory.CreateDirectory(_outputPath);
        }

        [Theory]
        [MemberData(nameof(GetAllDocuments))]
        public void RenderToFolder(string docPath)
        {
            string rootName = docPath.Substring(0, docPath.Length - 4);

            using (var document = PdfDocument.Open(Path.Combine("Documents", docPath)))
            {
                document.AddSkiaPageFactory();

                for (int p = 1; p <= document.NumberOfPages; p++)
                {
                    using (var fs = new FileStream(Path.Combine(_outputPath, $"{rootName}_{p}.png"), FileMode.Create))
                    using (var ms = document.GetPageAsPng(p, _scale, RGBColor.White))
                    {
                        ms.WriteTo(fs);
                    }
                }
            }
        }
    }
}
