using BenchmarkDotNet.Attributes;
using JBig2;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Filters;
using UglyToad.PdfPig.Filters.Dct.JpegLibrary;
using UglyToad.PdfPig.Filters.Jpx.OpenJpeg;
using UglyToad.PdfPig.Rendering.Skia;
using UglyToad.PdfPig.Tokens;

[MemoryDiagnoser]
public class Jbig2FilterBenchmarks
{
    private const string _pdfFile = "全国临床检验操作规程（第4版）.pdf";

    [Benchmark(Baseline = true)]
    public int Pdfbox()
    {
        int count = 0;
        using (var document = PdfDocument.Open(Path.Combine("SpecificTestDocuments", _pdfFile), SkiaRenderingParsingOptions.Instance))
        {
            document.AddSkiaPageFactory();

            for (int p = 1; p <= 20; ++p)
            {
                foreach (var image in document.GetPage(p).GetImages())
                {
                    count += image.BitsPerComponent;
                }
            }
        }

        return count;
    }

    [Benchmark]
    public int JPedal()
    {
        int count = 0;
        using (var document = PdfDocument.Open(Path.Combine("SpecificTestDocuments", _pdfFile), JPedalSkiaRenderingParsingOptions.Instance))
        {
            document.AddSkiaPageFactory();

            for (int p = 1; p <= 20; ++p)
            {
                foreach (var image in document.GetPage(p).GetImages())
                {
                    count += image.BitsPerComponent;
                }
            }
        }

        return count;
    }

    public static class JPedalSkiaRenderingParsingOptions
    {
        public static readonly ParsingOptions Instance = new ParsingOptions()
        {
            UseLenientParsing = true,
            SkipMissingFonts = true,
            FilterProvider = JPedalSkiaRenderingFilterProvider.Instance
        };
    }

    public sealed class JPedalSkiaRenderingFilterProvider : BaseFilterProvider
    {
        /// <summary>
        /// The single instance of this provider.
        /// </summary>
        public static readonly JPedalSkiaRenderingFilterProvider Instance = new JPedalSkiaRenderingFilterProvider();

        /// <inheritdoc/>
        private JPedalSkiaRenderingFilterProvider() : base(GetDictionary())
        {
        }

        private static Dictionary<string, IFilter> GetDictionary()
        {
            // New filters
            var dct = new JpegLibraryDctDecodeFilter();
            //var jbig2 = new PdfboxJbig2DecodeFilter();
            var jbig2 = new JPedalJbig2DecodeFilter();
            var jpx = new OpenJpegJpxDecodeFilter();

            // Standard filters
            var ascii85 = new Ascii85Filter();
            var asciiHex = new AsciiHexDecodeFilter();
            var ccitt = new CcittFaxDecodeFilter();
            var flate = new FlateFilter();
            var runLength = new RunLengthFilter();
            var lzw = new LzwFilter();

            return new Dictionary<string, IFilter>
                {
                    { NameToken.Ascii85Decode.Data, ascii85 },
                    { NameToken.Ascii85DecodeAbbreviation.Data, ascii85 },
                    { NameToken.AsciiHexDecode.Data, asciiHex },
                    { NameToken.AsciiHexDecodeAbbreviation.Data, asciiHex },
                    { NameToken.CcittfaxDecode.Data, ccitt },
                    { NameToken.CcittfaxDecodeAbbreviation.Data, ccitt },
                    { NameToken.DctDecode.Data, dct },
                    { NameToken.DctDecodeAbbreviation.Data, dct },
                    { NameToken.FlateDecode.Data, flate },
                    { NameToken.FlateDecodeAbbreviation.Data, flate },
                    { NameToken.Jbig2Decode.Data, jbig2 },
                    { NameToken.JpxDecode.Data, jpx },
                    { NameToken.RunLengthDecode.Data, runLength },
                    { NameToken.RunLengthDecodeAbbreviation.Data, runLength },
                    { NameToken.LzwDecode.Data, lzw },
                    { NameToken.LzwDecodeAbbreviation.Data, lzw }
                };
        }
    }
}