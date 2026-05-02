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

using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Environments;
using BenchmarkDotNet.Jobs;

namespace UglyToad.PdfPig.Rendering.Skia.Benchmarks;

internal class NuGetPackageConfig : ManualConfig
{
    public NuGetPackageConfig()
    {
        var baseJob = Job.Default;

        var localJob = baseJob
            .WithMsBuildArguments("/p:PdfPigSkiaVersion=Local")
            .WithId("Local");

        var latestJob = baseJob
            .WithMsBuildArguments("/p:PdfPigSkiaVersion=Latest")
            .WithId("Latest")
            .AsBaseline();

        AddJob(localJob.WithRuntime(CoreRuntime.Core80));
        AddJob(latestJob.WithRuntime(CoreRuntime.Core80));
    }
}
