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

using System;
using SkiaSharp;
using SkiaSharp.HarfBuzz;

namespace UglyToad.PdfPig.Rendering.Skia.Helpers
{
    internal sealed class SkiaFontCacheItem : IDisposable
    {
        public SkiaFontCacheItem(SKTypeface typeface)
        {
            if (typeface is null)
            {
                throw new ArgumentNullException(nameof(typeface));
            }

            Typeface = typeface;
            Shaper = new SKShaper(Typeface);
        }

        public SKTypeface Typeface { get; }

        public SKShaper Shaper { get; }

        public void Dispose()
        {
            Typeface.Dispose();
            Shaper.Dispose();
        }
    }
}
