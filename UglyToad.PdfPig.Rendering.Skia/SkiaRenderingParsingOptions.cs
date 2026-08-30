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

using UglyToad.PdfPig.Graphics.Colors.Icc;
using UglyToad.PdfPig.Rendering.Skia.Icc.Unicolour;

namespace UglyToad.PdfPig.Rendering.Skia
{
    /// <summary>
    /// Configures options used by the parser when reading PDF documents, with settings
    /// specific to skia rendering.
    /// <para>
    /// Whether device colours are colour-managed through the document's output intent is not configured
    /// here. Applying an output intent is impossible without a service to parse its profile, so the two are
    /// inseparable and <see cref="ParsingOptions.IccProfileService"/> owns the choice - supply
    /// <see cref="UnicolourIccProfileService.InstanceWithIntent"/> instead of
    /// <see cref="UnicolourIccProfileService.Instance"/>. See
    /// <see cref="IIccProfileService.UseOutputIntent"/>.
    /// </para>
    /// </summary>
    public static class SkiaRenderingParsingOptions
    {
        public static readonly ParsingOptions Instance = new()
        {
            UseLenientParsing = true,
            SkipMissingFonts = true,
            FilterProvider = SkiaRenderingFilterProvider.Instance,
            IccProfileService = UnicolourIccProfileService.Instance
        };
    }
}
