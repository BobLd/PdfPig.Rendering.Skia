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
using SkiaSharp;
using UglyToad.PdfPig.Graphics.Colors.Icc;
using UglyToad.PdfPig.Rendering.Skia.Icc.Unicolour;
using Xunit;

namespace UglyToad.PdfPig.Rendering.Skia.Tests
{
    public class OutputIntentTests
    {
        // GWG130 is a PDF/X-4 file whose green panel backgrounds are painted with
        // DeviceCMYK (.85 .03 1 .15). Per PDF/X semantics these must be interpreted
        // through the file's output intent profile (ISO Coated v2 300% ECI), which
        // yields roughly sRGB (0, 158, 46). The legacy ApproximateCmykToRgb formula
        // instead produced a dull (28, 125, 33), making the colour-managed green "X"
        // shapes stand out as a faint green cross.
        [Fact]
        public void DeviceCmykBackgroundUsesOutputIntent()
        {
            string path = Path.Combine(Helper.DocumentsFolder, "GWG130_ICC_Source_Profile_x4.pdf");

            ParsingOptions options = new()
            {
                UseLenientParsing = true,
                SkipMissingFonts = true,
                FilterProvider = SkiaRenderingFilterProvider.Instance,
                IccProfileService = UnicolourIccProfileService.InstanceWithIntent
            };
            
            using var document = PdfDocument.Open(path, options);
            document.AddSkiaPageFactory();

            using SKBitmap bitmap = document.GetPageAsSKBitmap(1, 2);

            (byte r, byte g, byte b) = DominantGreen(bitmap);

            // Output-intent managed background green: red channel is near zero and the
            // green channel is high. The crude approximation gives (28, 125, 33).
            Assert.True(r < 12, $"Background red channel too high ({r}); device CMYK not colour-managed via output intent.");
            Assert.True(g > 150, $"Background green channel too low ({g}); device CMYK not colour-managed via output intent.");
        }

        // With a service that does not opt into IIccProfileService.UseOutputIntent the device CMYK background must
        // fall back to the built-in ApproximateCmykToRgb conversion (~28, 125, 33), proving the output
        // intent is honoured only when opted in (14.11.5: output intents may be disregarded).
        [Fact]
        public void DeviceCmykBackgroundIgnoresOutputIntentWhenDisabled()
        {
            string path = Path.Combine(Helper.DocumentsFolder, "GWG130_ICC_Source_Profile_x4.pdf");

            var options = new ParsingOptions()
            {
                UseLenientParsing = true,
                SkipMissingFonts = true,
                FilterProvider = SkiaRenderingFilterProvider.Instance,
                IccProfileService = UnicolourIccProfileService.Instance
            };

            using var document = PdfDocument.Open(path, options);
            document.AddSkiaPageFactory();

            using SKBitmap bitmap = document.GetPageAsSKBitmap(1, 2);

            (byte r, byte g, byte b) = DominantGreen(bitmap);

            // Built-in approximation: a dull green with a clearly non-zero red channel.
            Assert.True(r > 18, $"Background red channel too low ({r}); output intent appears to have been applied despite being disabled.");
            Assert.True(g < 145, $"Background green channel too high ({g}); output intent appears to have been applied despite being disabled.");
        }

        // The subtype a caller prefers when a file declares several output intents. PDF/X is the standard
        // choice for the question this option asks - which entry characterizes the output device - so it is
        // the default, and the default selection is what it has always been.
        [Fact]
        public void PreferredOutputIntentSubtypeDefaultsToPdfX()
        {
            Assert.Equal(OutputIntent.PdfXSubtype, UnicolourIccProfileService.Instance.PreferredOutputIntentSubtype);
            Assert.Equal(OutputIntent.PdfXSubtype, UnicolourIccProfileService.InstanceWithIntent.PreferredOutputIntentSubtype);

            // And only the dedicated instance opts into applying it.
            Assert.False(UnicolourIccProfileService.Instance.UseOutputIntent);
            Assert.True(UnicolourIccProfileService.InstanceWithIntent.UseOutputIntent);
        }

        // A preference reorders and never filters: asking for a subtype the file does not declare must leave
        // the built-in order in charge, not disable colour management. GWG130 declares a PDF/X intent only,
        // so preferring PDF/E has to render exactly as DeviceCmykBackgroundUsesOutputIntent does.
        [Fact]
        public void AnUnknownPreferredSubtypeStillColourManages()
        {
            string path = Path.Combine(Helper.DocumentsFolder, "GWG130_ICC_Source_Profile_x4.pdf");

            var options = new ParsingOptions
            {
                UseLenientParsing = true,
                SkipMissingFonts = true,
                FilterProvider = SkiaRenderingFilterProvider.Instance,
                IccProfileService = new UnicolourIccProfileService(
                    useOutputIntent: true,
                    preferredOutputIntentSubtype: "ISO_PDFE1")
            };

            using var document = PdfDocument.Open(path, options);
            document.AddSkiaPageFactory();

            using SKBitmap bitmap = document.GetPageAsSKBitmap(1, 2);

            (byte r, byte g, byte b) = DominantGreen(bitmap);

            Assert.True(r < 12, $"Background red channel too high ({r}); an unmatched preference disabled colour management.");
            Assert.True(g > 150, $"Background green channel too low ({g}); an unmatched preference disabled colour management.");
        }

        private static (byte r, byte g, byte b) DominantGreen(SKBitmap bitmap)
        {
            var counts = new Dictionary<int, int>();
            for (int y = 0; y < bitmap.Height; y++)
            {
                for (int x = 0; x < bitmap.Width; x++)
                {
                    SKColor c = bitmap.GetPixel(x, y);
                    // greenish = green dominant and reasonably saturated
                    if (c.Green > 100 && c.Green > c.Red && c.Green > c.Blue)
                    {
                        int key = (c.Red << 16) | (c.Green << 8) | c.Blue;
                        counts.TryGetValue(key, out int n);
                        counts[key] = n + 1;
                    }
                }
            }

            int best = 0, bestKey = 0;
            foreach (var kv in counts)
            {
                if (kv.Value > best)
                {
                    best = kv.Value;
                    bestKey = kv.Key;
                }
            }

            return ((byte)((bestKey >> 16) & 0xFF), (byte)((bestKey >> 8) & 0xFF), (byte)(bestKey & 0xFF));
        }
    }
}
