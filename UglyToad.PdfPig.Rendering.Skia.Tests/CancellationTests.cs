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
using System.IO;
using System.Threading;
using Xunit;

namespace UglyToad.PdfPig.Rendering.Skia.Tests;

public class CancellationTests
{
    [Fact]
    public void PageTimedOut()
    {
        using (var document = PdfDocument.Open(Path.Combine(Helper.SpecificTestDocumentsFolder, "map.pdf"), SkiaRenderingParsingOptions.Instance))
        {
            document.AddSkiaPageFactory();

            // Get page but tine out
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            {
                // Page 10 will run (virtually) forever
                Assert.Throws<OperationCanceledException>(() =>
                {
                    document.GetPageAsSKPicture(10, cts.Token);
                });
            }

            // Get page
            using (var cts = new CancellationTokenSource())
            {
                var picture = document.GetPageAsSKPicture(1, cts.Token);
                Assert.NotNull(picture);
                Assert.True(picture.ApproximateOperationCount > 0);
            }
        }
    }

    [Fact]
    public void CancelBeforeStart()
    {
        using (var document = PdfDocument.Open(Path.Combine(Helper.SpecificTestDocumentsFolder, "new.pdf"), SkiaRenderingParsingOptions.Instance))
        {
            document.AddSkiaPageFactory();
            
            // Get page but cancel
            using (var cts = new CancellationTokenSource())
            {
                cts.Cancel();
                Assert.Throws<OperationCanceledException>(() =>
                {
                    document.GetPageAsSKPicture(1, cts.Token);
                });
            }

            // Get page
            using (var cts = new CancellationTokenSource())
            {
                var picture = document.GetPageAsSKPicture(1, cts.Token);
                Assert.NotNull(picture);
                Assert.True(picture.ApproximateOperationCount > 0);
            }
        }
    }
}