using UglyToad.PdfPig.Icc.Unicolour;
using System;
using System.IO;
using UglyToad.PdfPig.Graphics.Core;
using Xunit;

namespace UglyToad.PdfPig.Rendering.Skia.Tests;

public class UnicolourIccProfileServiceTests
{
    // The sRGB2014 ICC profile is embedded inside UglyToad.PdfPig itself
    // (Resources/ICC/sRGB2014.icc). Pull it out as raw bytes so we can
    // exercise the real Unicolour-backed conversion path.
    private static byte[] LoadEmbeddedSrgbProfile()
    {
        var pdfPigAssembly = typeof(global::UglyToad.PdfPig.PdfDocument).Assembly;
        const string name = "UglyToad.PdfPig.Resources.ICC.sRGB2014.icc";
        using var stream = pdfPigAssembly.GetManifestResourceStream(name);
        if (stream is null)
        {
            throw new InvalidOperationException(
                $"Embedded sRGB profile not found. Available: {string.Join(", ", pdfPigAssembly.GetManifestResourceNames())}");
        }
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    [Fact]
    public void TryGetProfile_LoadsRealSrgbProfile()
    {
        byte[] bytes = LoadEmbeddedSrgbProfile();
        Assert.True(bytes.Length > 100, "sRGB profile bytes are too short.");

        bool ok = UnicolourIccProfileService.Default.TryGetProfile(
            bytes.AsMemory(), 3, out var profile);

        Assert.True(ok, "Real sRGB ICC profile should parse via Unicolour.");
        Assert.NotNull(profile);
        Assert.Equal(3, profile!.NumberOfComponents);
    }

    [Fact]
    public void TryGetProfile_DoesNotMutateInputBytes()
    {
        // Regression: CalculateId used to zero bytes 44-99 in the caller's
        // buffer, then pass the same corrupted buffer to the Profile parser.
        // The mutated header fields (rendering intent, PCS illuminant,
        // creator, profile ID) caused some profiles to fail parsing or
        // produce wrong output.
        byte[] bytes = LoadEmbeddedSrgbProfile();
        byte[] snapshot = (byte[])bytes.Clone();

        _ = UnicolourIccProfileService.Default.TryGetProfile(
            bytes.AsMemory(), 3, out _);

        Assert.True(bytes.AsSpan().SequenceEqual(snapshot.AsSpan()),
            "Profile bytes must not be mutated by TryGetProfile.");
    }

    [Fact]
    public void ToRgb_ClipsOutOfGamutComponentsToZeroOrOne()
    {
        // Regression for the GWG130 ICC Source Profile bug: ICC conversion
        // from CMYK (and other wide-gamut sources) can produce sRGB
        // components outside [0,1]. We must clip so downstream code
        // (RGBColor → ToSKColor) doesn't treat the raw doubles as bytes.
        //
        // Construct a CMYK profile and convert a saturated CMYK value
        // that's known to fall outside sRGB gamut. We use a process CMYK
        // profile if available; otherwise approximate by checking that
        // the sRGB→sRGB pass also clamps anything outside [0,1].
        byte[] bytes = LoadEmbeddedSrgbProfile();
        Assert.True(UnicolourIccProfileService.Default.TryGetProfile(
            bytes.AsMemory(), 3, out var profile));
        Assert.True(profile!.TryGetTransform(RenderingIntent.RelativeColorimetric, out var t));

        // sRGB→sRGB normally yields in-gamut values, so push the input
        // outside [0,1] to force a wider-than-gamut result; the adapter
        // must still emit values in [0,1].
        var (r, g, b) = t!.ToRgb(new double[] { 1.5, -0.2, 0.5 });

        Assert.InRange(r, 0.0, 1.0);
        Assert.InRange(g, 0.0, 1.0);
        Assert.InRange(b, 0.0, 1.0);
    }

    [Fact]
    public void Transform_SrgbProfile_RoundTripsApproximately()
    {
        byte[] bytes = LoadEmbeddedSrgbProfile();
        Assert.True(UnicolourIccProfileService.Default.TryGetProfile(
            bytes.AsMemory(), 3, out var profile));

        Assert.True(profile!.TryGetTransform(RenderingIntent.RelativeColorimetric, out var t));

        // An sRGB→sRGB transform should round-trip near-identity for
        // mid-grey. Tolerance is generous because ICC tables and gamma
        // handling introduce small deltas.
        var (r, g, b) = t!.ToRgb(new double[] { 0.5, 0.5, 0.5 });

        Assert.InRange(r, 0.40, 0.60);
        Assert.InRange(g, 0.40, 0.60);
        Assert.InRange(b, 0.40, 0.60);

        // Black stays black.
        (r, g, b) = t.ToRgb(new double[] { 0.0, 0.0, 0.0 });
        Assert.InRange(r, 0.0, 0.05);
        Assert.InRange(g, 0.0, 0.05);
        Assert.InRange(b, 0.0, 0.05);

        // White stays white.
        (r, g, b) = t.ToRgb(new double[] { 1.0, 1.0, 1.0 });
        Assert.InRange(r, 0.95, 1.001);
        Assert.InRange(g, 0.95, 1.001);
        Assert.InRange(b, 0.95, 1.001);
    }
}
