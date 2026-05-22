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
public class RenderPagesBenchmarks
{
    [Benchmark]
    public IReadOnlyList<SKPicture> MOZILLA_LINK_3264_0()
    {
        return Helpers.RenderAllPages("MOZILLA-LINK-3264-0.pdf");
    }

    [Benchmark]
    public IReadOnlyList<SKPicture> GHOSTSCRIPT_693120_0()
    {
        return Helpers.RenderAllPages("GHOSTSCRIPT-693120-0.pdf");
    }

    [Benchmark]
    public IReadOnlyList<SKPicture> DeviceN_CS_test()
    {
        return Helpers.RenderAllPages("DeviceN_CS_test.pdf");
    }

    [Benchmark]
    public IReadOnlyList<SKPicture> GHOSTSCRIPT_693295_0()
    {
        return Helpers.RenderAllPages("GHOSTSCRIPT-693295-0.pdf");
    }

    [Benchmark]
    public IReadOnlyList<SKPicture> GHOSTSCRIPT_693154_0()
    {
        return Helpers.RenderAllPages("GHOSTSCRIPT-693154-0.pdf");
    }
}
