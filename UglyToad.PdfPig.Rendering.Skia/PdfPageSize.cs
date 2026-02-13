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

namespace UglyToad.PdfPig.Rendering.Skia;

/// <summary>
/// Page size information for a PDF page, including the page number, height, and width.
/// </summary>
public readonly struct PdfPageSize
{
    internal PdfPageSize(int pageNumber, double width, double height)
    {
        PageNumber = pageNumber;
        Width = width;
        Height = height;
    }

    /// <summary>
    /// Page number, starting from 1.
    /// </summary>
    public int PageNumber { get; }

    /// <summary>
    /// Page height, as defined in the document, in points (1/72 inch).
    /// </summary>
    public double Height { get; }

    /// <summary>
    /// Page width, as defined in the document, in points (1/72 inch).
    /// </summary>
    public double Width { get; }
}
