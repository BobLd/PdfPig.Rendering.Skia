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

using Caly.PdfPig.Fonts;
using UglyToad.PdfPig.Rendering.Skia.Tests;
using Xunit;

[assembly: AssemblyFixture(typeof(StandardFontSubstitutesFixture))]

namespace UglyToad.PdfPig.Rendering.Skia.Tests;

/// <summary>
/// Registers the bundled Standard 14 substitute fonts once, before any test in the assembly runs.
/// <para>
/// The registration is process-wide, so it must happen before the first document is rendered by any test
/// class, not lazily from one of them: the golden images expect the substitutes, and a registration landing
/// while another class renders in parallel would make that render (and <see cref="ConcurrencyTests"/>)
/// order-dependent.
/// </para>
/// </summary>
public sealed class StandardFontSubstitutesFixture
{
    public StandardFontSubstitutesFixture()
    {
        StandardFontSubstitutes.Register();
    }
}