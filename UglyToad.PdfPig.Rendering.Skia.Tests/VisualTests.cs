// Copyright 2024 BobLd
//
// Licensed under the Apache License, Version 2.0 (the "License").
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
// http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

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

        private static readonly HashSet<string> _documentsToIgnore = new HashSet<string>
        {
            "GHOSTSCRIPT-699178-0.pdf",
            "SPARC - v9 Architecture Manual.pdf",
            "TIKA-1552-0.pdf"
        };

        public static IEnumerable<object[]> GetAllDocuments => Directory.EnumerateFiles("Documents", "*.pdf")
            .Select(Path.GetFileName)
            .Where(p => !_documentsToIgnore.Contains(p))
            .Select(p => new object[] { p });

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
