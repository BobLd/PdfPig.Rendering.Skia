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

    public static TheoryData<string> EnumerateDocuments(ICollection<string> exclude)
    {
        var data = new TheoryData<string>();

        foreach (string name in Directory.EnumerateFiles(DocumentsFolder, "*.pdf")
                     .Select(Path.GetFileName)
                     .Where(name => name is not null && !exclude.Contains(name))
                     .OrderBy(name => name, StringComparer.Ordinal)!)
        {
            data.Add(name);
        }

        return data;
    }
}