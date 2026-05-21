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

using BenchmarkDotNet.Attributes;
using SkiaSharp;

namespace UglyToad.PdfPig.Rendering.Skia.Benchmarks;

[Config(typeof(NuGetPackageConfig))]
[MemoryDiagnoser(displayGenColumns: false)]
public class ShadingBenchmarks
{
    [ParamsSource(nameof(Documents))]
    public string Document { get; set; }

    public static IEnumerable<string> Documents => new[]
    {
        "GWG060_Shading_x1a.pdf",
        "GWG061_Shading_x1a.pdf",
        "2_shading_type1.pdf",
        "2_shading_type1_sc_.pdf",
        "2_shading_type3.pdf",
        "2_shading_type4_h.pdf",
        "2_shading_type5_h.pdf",
        "2_shading_type_6_001.pdf",
        "P.pdf",
        "PDFBOX-1869-4.pdf",
        "PDFBOX-2100-gouraud-RGB-function.pdf"
    };

    [Benchmark]
    public IReadOnlyList<SKPicture> GWG060_Shading_x1a()
    {
        return Helpers.RenderAllPages(Document);
    }
}