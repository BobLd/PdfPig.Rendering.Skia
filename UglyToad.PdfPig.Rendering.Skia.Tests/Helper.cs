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
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace UglyToad.PdfPig.Rendering.Skia.Tests;

internal static class Helper
{
    public static string DocumentsFolder => Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "Documents"));

    public static string ExpectedImagesFolder => Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "ExpectedImages"));

    public static string SpecificTestDocumentsFolder => Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "SpecificTestDocuments"));

    /// <summary>
    /// Whether the document can be opened by the current target framework.
    /// <para>
    /// PdfPig only implements the <c>BrotliDecode</c> filter on .NET Standard 2.1, .NET Core and
    /// .NET 5.0 or greater targets - opening a document that uses it on .NET Framework throws
    /// <see cref="NotSupportedException"/>.
    /// </para>
    /// </summary>
    public static bool IsSupportedOnCurrentFramework(string name)
    {
#if NET
        return true;
#else
        return !name.StartsWith("Brotli-", StringComparison.Ordinal);
#endif
    }

    public static TheoryData<string> EnumerateDocuments(ICollection<string> exclude)
    {
        var data = new TheoryData<string>();

        foreach (string name in Directory.EnumerateFiles(DocumentsFolder, "*.pdf")
                     .Select(Path.GetFileName)
                     .Where(name => name is not null && !exclude.Contains(name) && IsSupportedOnCurrentFramework(name))
                     .OrderBy(name => name, StringComparer.Ordinal)!)
        {
            data.Add(name);
        }

        return data;
    }
}