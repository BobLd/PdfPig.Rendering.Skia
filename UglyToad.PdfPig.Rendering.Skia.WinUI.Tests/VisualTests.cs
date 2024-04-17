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
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;
using ImageMagick;
using OpenCvSharp;
using SkiaSharp;
using NativePDF = Windows.Data.Pdf;
using UglyToad.PdfPig.Graphics.Colors;

namespace UglyToad.PdfPig.Rendering.Skia.WinUI.Tests
{
    [TestClass]
    public class VisualTests
    {
        private const int _scale = 2;
        private const string _outputPath = "Output";

        public static IEnumerable<object[]> GetAllDocuments()
        {
            return Directory.EnumerateFiles("Documents", "*.pdf")
                .Select(Path.GetFileName)
                .Where(p => !p.EndsWith("GHOSTSCRIPT-699178-0.pdf")) // Seems to be an issue with PdfPig
                .Select(p => new object[] { p });
        }

        [TestInitialize]
        public void Initialize()
        {
            Directory.CreateDirectory(_outputPath);
        }

        [TestMethod]
        [DynamicData(nameof(GetAllDocuments), DynamicDataSourceType.Method)]
        public void RenderToFolderSkia(string docPath)
        {
            string rootName = Path.GetFileNameWithoutExtension(docPath);
            Directory.CreateDirectory(Path.Combine(_outputPath, $"{rootName}"));

            using var document = PdfDocument.Open(Path.Combine("Documents", docPath));
            document.AddSkiaPageFactory();

            for (int p = 1; p <= document.NumberOfPages; p++)
            {
                using var fs = new FileStream(Path.Combine(_outputPath, $"{rootName}", $"{rootName}_skia_{p}.png"),
                    FileMode.Create);
                using var ms = document.GetPageAsPng(p, _scale, RGBColor.White);
                // using var skpic = document.GetPage<SKPicture>(p);
                ms.WriteTo(fs);
            }
        }


        [TestMethod]
        [DynamicData(nameof(GetAllDocuments), DynamicDataSourceType.Method)]
        public void RenderToFolder(string docPath)
        {
            string rootName = Path.GetFileNameWithoutExtension(docPath);

            Directory.CreateDirectory(Path.Combine(_outputPath, $"{rootName}"));


            var absolutePath = Path.GetFullPath(Path.Combine(@"Documents", docPath));
            var storageFile = StorageFile.GetFileFromPathAsync(absolutePath).AsTask().Result;
            //
            var document = NativePDF.PdfDocument.LoadFromFileAsync(storageFile).AsTask().Result;

            for (var i = 0; i < document.PageCount; ++i)
            {
                var page = document.GetPage((uint)i);

                var renderOptions = new PdfPageRenderOptions
                {
                    DestinationHeight = (uint)(page.Size.Height ),
                    DestinationWidth = (uint)(page.Size.Width ),
                    
                };

                var outputStream = new InMemoryRandomAccessStream();
                page.RenderToStreamAsync(outputStream, renderOptions).AsTask().Wait();

                var file = File.OpenWrite(Path.Combine(_outputPath, $"{rootName}", $"{rootName}_native_{i}.png"));
                outputStream.AsStreamForRead().CopyTo(file);

            }
        }





        //using image magick to compare the two images
        [TestMethod]
        [DynamicData(nameof(GetAllDocuments), DynamicDataSourceType.Method)]
        public void CompareRenderPicsMagick(string docPath)
        {
            string rootName = Path.GetFileNameWithoutExtension(docPath);
            Directory.CreateDirectory(Path.Combine(_outputPath, $"{rootName}"));

            var skiaFiles = Directory.GetFiles(Path.Combine(_outputPath, $"{rootName}"), "*_skia_*.png");
            var nativeFiles = Directory.GetFiles(Path.Combine(_outputPath, $"{rootName}"), "*_native_*.png");

            Assert.AreEqual(skiaFiles.Length, nativeFiles.Length);

            for (int i = 0; i < skiaFiles.Length; i++)
            {
                var skiaFile = skiaFiles[i];
                var nativeFile = nativeFiles[i];

                var skiaBytes = File.ReadAllBytes(skiaFile);
                var nativeBytes = File.ReadAllBytes(nativeFile);

                using var skiaImage = new MagickImage(skiaBytes);
                using var nativeImage = new MagickImage(nativeBytes);
                // https://imagemagick.org/script/compare.php for parameters ref
                // Set up comparison settings
                // var diffMask = nativeImage.Clone();
                // diffMask.Composite(nativeImage, CompositeOperator.Difference, Channels.RGB);
                // var changed = skiaImage.Clone();
                // changed.Composite(nativeImage, CompositeOperator.CopyAlpha);
                using var changed=new MagickImage();

                skiaImage.Compare(nativeImage, ErrorMetric.StructuralSimilarity, changed);

                var file = File.OpenWrite(Path.Combine(_outputPath, $"{rootName}", $"MAGICK_Compare_{rootName}_{i}.png"));
              
                file.Write(changed.ToByteArray());


                // Assert.AreEqual(skiaBytes.Length, nativeBytes.Length)
            }
        }


        //using opencv
        [TestMethod]
        [DynamicData(nameof(GetAllDocuments), DynamicDataSourceType.Method)]
        public void CompareRenderPicsOpencv(string docPath)
        {
            string rootName = Path.GetFileNameWithoutExtension(docPath);
            Directory.CreateDirectory(Path.Combine(_outputPath, $"{rootName}"));

            var skiaFiles = Directory.GetFiles(Path.Combine(_outputPath, $"{rootName}"), "*_skia_*.png");
            var nativeFiles = Directory.GetFiles(Path.Combine(_outputPath, $"{rootName}"), "*_native_*.png");

            Assert.AreEqual(skiaFiles.Length, nativeFiles.Length);
            for (int i = 0; i < skiaFiles.Length; i++)
            {
                var skiaFile = Path.GetFullPath(skiaFiles[i]);
                var nativeFile = Path.GetFullPath(nativeFiles[i]);

                // using var skiaImage = new Mat(skiaFile, ImreadModes.Unchanged);
                // using var nativeImage = new Mat(nativeFile, ImreadModes.Unchanged);
                // using var diffImage = new Mat();
                // var diffImage=skiaImage - nativeImage;
                var skiaImage = Cv2.ImRead(skiaFile,ImreadModes.Grayscale);
                var nativeImage = Cv2.ImRead(nativeFile,ImreadModes.Grayscale);
                var diffImage = new Mat();
                Cv2.Absdiff(skiaImage, nativeImage, diffImage);
                var thresholdedDiff = new Mat();
                Cv2.Threshold(diffImage, thresholdedDiff, 30, 255, ThresholdTypes.Binary);
                var file = Path.Combine(_outputPath, $"{rootName}", $"OPENCV_Compare_{rootName}_{i}.png");
                Cv2.ImWrite(file, thresholdedDiff);

            }

        }

    }
}