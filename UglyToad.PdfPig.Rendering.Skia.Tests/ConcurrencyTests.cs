// Copyright BobLd
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
using System.Threading.Tasks;
using SkiaSharp;
using Xunit;

namespace UglyToad.PdfPig.Rendering.Skia.Tests;

public class ConcurrencyTests
{
    /// <summary>
    /// Every SkiaFontCache shares the process-global native font manager
    /// (SKFontManager.CreateDefault() wraps SkFontMgr::RefDefault()). On macOS the CoreText-backed
    /// manager resolves character fallback non-deterministically under concurrent access: two
    /// threads matching the same CJK codepoint can pick different — though near-identical — system
    /// Han fonts, which showed up as intermittent image-regression failures on the macOS CI runners
    /// (Rotation 45 / Page_28 / VerticalText, all CJK-fallback-heavy). SkiaFontCache serialises
    /// native fallback resolution behind a static lock; this test guards that by rendering the
    /// affected documents from many documents in parallel and requiring byte-identical pixels.
    /// </summary>
    [Fact]
    public void ParallelDocuments_SameCjkFallbackPage_RenderIdentically()
    {
        const int rendersPerDocument = 4;

        string[] documents =
        [
            "Rotation 45.pdf",
            "Page_28.pdf",
            "VerticalText.pdf"
        ];

        var work = documents
            .SelectMany(d => Enumerable.Range(0, rendersPerDocument).Select(i => (Document: d, Slot: i)))
            .ToArray();

        var results = new Dictionary<string, byte[][]>();
        foreach (string document in documents)
        {
            results[document] = new byte[rendersPerDocument][];
        }

        // Maximum contention: all renders (across all documents) race on the shared native
        // font manager at once, each through its own document-scoped SkiaFontCache.
        Parallel.ForEach(work, new ParallelOptions { MaxDegreeOfParallelism = work.Length }, item =>
        {
            using (var pdf = PdfDocument.Open(
                       Path.Combine(Helper.DocumentsFolder, item.Document),
                       SkiaRenderingParsingOptions.Instance))
            {
                pdf.AddSkiaPageFactory();
                using (SKBitmap bitmap = pdf.GetPageAsSKBitmap(1, 2, SKColors.White))
                {
                    results[item.Document][item.Slot] = bitmap.GetPixelSpan().ToArray();
                }
            }
        });

        foreach (string document in documents)
        {
            byte[][] renders = results[document];
            for (int i = 1; i < renders.Length; ++i)
            {
                Assert.True(renders[0].SequenceEqual(renders[i]),
                    $"{document}: parallel render {i} differs from render 0 — " +
                    "font fallback resolution is not deterministic under concurrency.");
            }
        }
    }
}
