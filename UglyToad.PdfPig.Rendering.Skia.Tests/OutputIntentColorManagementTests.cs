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

using UglyToad.PdfPig.Graphics.Colors;
using UglyToad.PdfPig.Rendering.Skia.Helpers;
using Xunit;

namespace UglyToad.PdfPig.Rendering.Skia.Tests
{
    public class OutputIntentColorManagementTests
    {
        [Fact]
        public void MatchingComponentCount_PassesThrough()
        {
            bool ok = OutputIntentColorManagement.TryMapDeviceToProfileComponents(
                ColorSpace.DeviceCMYK, new[] { 0.1, 0.2, 0.3, 0.4 }, 4, out var mapped);

            Assert.True(ok);
            Assert.Equal(new[] { 0.1, 0.2, 0.3, 0.4 }, mapped);
        }

        [Fact]
        public void DeviceGray_ToCmykProfile_MapsToBlackChannel()
        {
            // grey 0.25 -> K = 1 - 0.25 = 0.75, C = M = Y = 0.
            bool ok = OutputIntentColorManagement.TryMapDeviceToProfileComponents(
                ColorSpace.DeviceGray, new[] { 0.25 }, 4, out var mapped);

            Assert.True(ok);
            Assert.Equal(new[] { 0.0, 0.0, 0.0, 0.75 }, mapped);
        }

        [Fact]
        public void DeviceGray_ToRgbProfile_Replicated()
        {
            bool ok = OutputIntentColorManagement.TryMapDeviceToProfileComponents(
                ColorSpace.DeviceGray, new[] { 0.25 }, 3, out var mapped);

            Assert.True(ok);
            Assert.Equal(new[] { 0.25, 0.25, 0.25 }, mapped);
        }

        [Fact]
        public void DeviceGray_ToGrayProfile_PassesThrough()
        {
            bool ok = OutputIntentColorManagement.TryMapDeviceToProfileComponents(
                ColorSpace.DeviceGray, new[] { 0.25 }, 1, out var mapped);

            Assert.True(ok);
            Assert.Equal(new[] { 0.25 }, mapped);
        }

        [Fact]
        public void DeviceRgb_ToCmykProfile_NotMapped()
        {
            // No well-defined neutral RGB -> CMYK mapping; left to the built-in conversion.
            bool ok = OutputIntentColorManagement.TryMapDeviceToProfileComponents(
                ColorSpace.DeviceRGB, new[] { 0.1, 0.2, 0.3 }, 4, out var mapped);

            Assert.False(ok);
            Assert.Null(mapped);
        }

        [Fact]
        public void DeviceCmyk_ToRgbProfile_NotMapped()
        {
            bool ok = OutputIntentColorManagement.TryMapDeviceToProfileComponents(
                ColorSpace.DeviceCMYK, new[] { 0.1, 0.2, 0.3, 0.4 }, 3, out var mapped);

            Assert.False(ok);
            Assert.Null(mapped);
        }
    }
}
