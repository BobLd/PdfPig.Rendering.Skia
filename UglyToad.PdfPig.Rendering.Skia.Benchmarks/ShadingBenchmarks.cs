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
    // Shading-type coverage per document (from raw `ShadingType N` scan of each PDF):
    //   "GWG060_Shading_x1a.pdf"                 -> 2 (axial), 3 (radial), 7 (tensor)
    //   "GWG061_Shading_x1a.pdf"                 -> 2 (axial), 3 (radial)
    //   "2_shading_type1.pdf"                    -> 1 (function-based)
    //   "2_shading_type1_sc_.pdf"                -> 1 (function-based, via shading-pattern colour)
    //   "2_shading_type3.pdf"                    -> 3 (radial)
    //   "2_shading_type4_h.pdf"                  -> 4 (free-form Gouraud)
    //   "2_shading_type5_h.pdf"                  -> 5 (lattice Gouraud)
    //   "2_shading_type_6_001.pdf"               -> 6 (Coons patch)
    //   "P.pdf"                                  -> 2, 4, 5, 6, 7 (covers every mesh shading)
    //   "PDFBOX-1869-4.pdf"                      -> 1 (function-based)
    //   "PDFBOX-2100-gouraud-RGB-function.pdf"   -> 4 (free-form Gouraud with Function)
    
    [Benchmark]
    public IReadOnlyList<SKPicture> _2_shading_type1()
    {
        return Helpers.RenderAllPages("2_shading_type1.pdf");
    }

    [Benchmark]
    public IReadOnlyList<SKPicture> _2_shading_type1_sc_()
    {
        return Helpers.RenderAllPages("2_shading_type1_sc_.pdf");
    }
    
    [Benchmark]
    public IReadOnlyList<SKPicture> _2_shading_type3()
    {
        return Helpers.RenderAllPages("2_shading_type3.pdf");
    }

    [Benchmark]
    public IReadOnlyList<SKPicture> _2_shading_type4_h()
    {
        return Helpers.RenderAllPages("2_shading_type4_h.pdf");
    }
    
    [Benchmark]
    public IReadOnlyList<SKPicture> _2_shading_type5_h()
    {
        return Helpers.RenderAllPages("2_shading_type5_h.pdf");
    }
    
    [Benchmark]
    public IReadOnlyList<SKPicture> _2_shading_type_6_001()
    {
        return Helpers.RenderAllPages("2_shading_type_6_001.pdf");
    }
    
    [Benchmark]
    public IReadOnlyList<SKPicture> P()
    {
        return Helpers.RenderAllPages("P.pdf");
    }
    
    [Benchmark]
    public IReadOnlyList<SKPicture> PDFBOX_1869_4()
    {
        return Helpers.RenderAllPages("PDFBOX-1869-4.pdf");
    }

    [Benchmark]
    public IReadOnlyList<SKPicture> PDFBOX_2100_gouraud_RGB_function()
    {
        return Helpers.RenderAllPages("PDFBOX-2100-gouraud-RGB-function.pdf");
    }

    [Benchmark]
    public IReadOnlyList<SKPicture> GWG060_Shading_x1a()
    {
        return Helpers.RenderAllPages("GWG060_Shading_x1a.pdf");
    }

    [Benchmark]
    public IReadOnlyList<SKPicture> GWG061_Shading_x1a()
    {
        return Helpers.RenderAllPages("GWG061_Shading_x1a.pdf");
    }
}