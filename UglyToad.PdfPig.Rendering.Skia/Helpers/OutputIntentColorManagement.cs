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

using System;
using UglyToad.PdfPig.Graphics.Colors;
using UglyToad.PdfPig.Graphics.Colors.Icc;
using UglyToad.PdfPig.Graphics.Core;

namespace UglyToad.PdfPig.Rendering.Skia.Helpers
{
    /// <summary>
    /// Helpers for colour-managing device colours through a document's output intent (PDF 2.0 14.11.5 /
    /// 8.6.5.7). Output-intent simulation is a rendering/output concern, so it lives in the renderer
    /// rather than in the core colour-interpretation layer.
    /// </summary>
    public static class OutputIntentColorManagement
    {
        /// <summary>
        /// Convert a device colour through an output intent profile to sRGB, returning <c>true</c> and the
        /// managed <see cref="RGBColor"/> when the colour is a device colour (DeviceGray / DeviceRGB /
        /// DeviceCMYK) expressible in the profile's colour space. Returns <c>false</c> (so the caller keeps
        /// the built-in conversion) for non-device colours, profile/device mismatches with no neutral
        /// mapping (e.g. DeviceRGB with a CMYK output intent) or when the transform cannot be built.
        /// </summary>
        public static bool TryConvert(IColor? color, ColorSpace colorSpaceType, IIccProfile profile,
            RenderingIntent intent, out IColor? managed)
        {
            managed = null;

            if (!TryGetDeviceComponents(color, colorSpaceType, out double[] values) ||
                !TryMapDeviceToProfileComponents(colorSpaceType, values, profile.NumberOfComponents, out double[]? deviceValues) ||
                !profile.TryGetTransform(intent, out var transform) ||
                transform is null)
            {
                return false;
            }

            var (r, g, b) = transform.ToRgb(deviceValues);
            managed = new RGBColor(r, g, b);
            return true;
        }

        /// <summary>
        /// Extract the device colour components from a device colour, or return <c>false</c> when the colour
        /// is not a device colour matching <paramref name="colorSpaceType"/>.
        /// </summary>
        private static bool TryGetDeviceComponents(IColor? color, ColorSpace colorSpaceType, out double[] values)
        {
            switch (colorSpaceType)
            {
                case ColorSpace.DeviceGray when color is GrayColor gray:
                    values = [gray.Gray];
                    return true;
                case ColorSpace.DeviceRGB when color is RGBColor rgb:
                    values = [rgb.R, rgb.G, rgb.B];
                    return true;
                case ColorSpace.DeviceCMYK when color is CMYKColor cmyk:
                    values = [cmyk.C, cmyk.M, cmyk.Y, cmyk.K];
                    return true;
                default:
                    values = Array.Empty<double>();
                    return false;
            }
        }

        /// <summary>
        /// Map device colour values onto the output intent profile's colour space. A device space whose
        /// component count already matches the profile passes through unchanged. <see cref="ColorSpace.DeviceGray"/>
        /// is expanded neutrally into a 3- or 4-component profile: grey <c>g</c> becomes <c>(g, g, g)</c> for an
        /// RGB profile, or <c>(0, 0, 0, 1 - g)</c> — the black channel — for a CMYK profile, so grey content
        /// shares the managed space. Other mismatches (notably DeviceRGB with a CMYK output intent, or
        /// DeviceCMYK with an RGB output intent) have no well-defined neutral mapping and return <c>false</c>.
        /// </summary>
        public static bool TryMapDeviceToProfileComponents(ColorSpace deviceType, ReadOnlySpan<double> values,
            int profileComponents, out double[]? mapped)
        {
            if (values.Length == profileComponents)
            {
                mapped = values.ToArray();
                return true;
            }

            if (deviceType == ColorSpace.DeviceGray && values.Length == 1)
            {
                double grey = values[0];
                mapped = profileComponents switch
                {
                    3 => [grey, grey, grey],
                    4 => [0.0, 0.0, 0.0, 1.0 - grey],
                    _ => null
                };

                return mapped is not null;
            }

            mapped = null;
            return false;
        }
    }
}
