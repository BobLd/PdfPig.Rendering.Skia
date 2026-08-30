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

#if NETSTANDARD2_0 || NETFRAMEWORK

namespace System.Diagnostics.CodeAnalysis
{
    /// <summary>
    /// Polyfill for the attribute the BCL only started shipping publicly in netstandard2.1.
    /// <para>
    /// It is needed, rather than merely nice to have, to implement a <c>Try...</c> method PdfPig declares as
    /// <c>[NotNullWhen(true)] out T?</c>: an implementation whose out parameter is nullable but carries no
    /// attribute promises less than the interface does and warns (CS8767), and the alternative - declaring
    /// the parameter non-nullable and assigning <c>null!</c> on the failure path - only hides that from the
    /// compiler. <c>System.Memory</c> does carry this type on these frameworks, but as an internal of its
    /// own assembly, so referencing it here is a compile error (CS0122) rather than a fallback.
    /// </para>
    /// </summary>
    [AttributeUsage(AttributeTargets.Parameter, Inherited = false)]
    internal sealed class NotNullWhenAttribute : Attribute
    {
        /// <summary>
        /// Create the attribute.
        /// </summary>
        /// <param name="returnValue">The return value for which the parameter is guaranteed not to be
        /// <see langword="null"/>.</param>
        public NotNullWhenAttribute(bool returnValue)
        {
            ReturnValue = returnValue;
        }

        /// <summary>
        /// The return value for which the parameter is guaranteed not to be <see langword="null"/>.
        /// </summary>
        public bool ReturnValue { get; }
    }
}

#endif
