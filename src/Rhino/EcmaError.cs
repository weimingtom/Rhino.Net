/*
 * This code is derived from rhino (http://github.com/mozilla/rhino)
 * 
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using Rhino;
using Sharpen;

namespace Rhino
{
	/// <summary>
	/// The class of exceptions raised by the engine as described in
	/// ECMA edition 3.
	/// </summary>
	/// <remarks>
	/// The class of exceptions raised by the engine as described in
	/// ECMA edition 3. See section 15.11.6 in particular.
	/// </remarks>
	[System.Serializable]
	public class EcmaError : RhinoException
	{
		private string errorName;

		private string errorMessage;

		/// <summary>Create an exception with the specified detail message.</summary>
		/// <remarks>
		/// Create an exception with the specified detail message.
		/// Errors internal to the JavaScript engine will simply throw a
		/// RuntimeException.
		/// </remarks>
		/// <param name="sourceName">the name of the source responsible for the error</param>
		/// <param name="lineNumber">the line number of the source</param>
		/// <param name="columnNumber">
		/// the columnNumber of the source (may be zero if
		/// unknown)
		/// </param>
		/// <param name="lineSource">
		/// the source of the line containing the error (may be
		/// null if unknown)
		/// </param>
		internal EcmaError(string errorName, string errorMessage, string sourceName, int lineNumber, string lineSource, int columnNumber)
		{
			// API class
			RecordErrorOrigin(sourceName, lineNumber, lineSource, columnNumber);
			this.errorName = errorName;
			this.errorMessage = errorMessage;
		}

		public override string Details()
		{
			return errorName + ": " + errorMessage;
		}

		/// <summary>Gets the name of the error.</summary>
		/// <remarks>
		/// Gets the name of the error.
		/// ECMA edition 3 defines the following
		/// errors: EvalError, RangeError, ReferenceError,
		/// SyntaxError, TypeError, and URIError. Additional error names
		/// may be added in the future.
		/// See ECMA edition 3, 15.11.7.9.
		/// </remarks>
		/// <returns>the name of the error.</returns>
		public virtual string GetName()
		{
			return errorName;
		}

		/// <summary>Gets the message corresponding to the error.</summary>
		/// <remarks>
		/// Gets the message corresponding to the error.
		/// See ECMA edition 3, 15.11.7.10.
		/// </remarks>
		/// <returns>an implementation-defined string describing the error.</returns>
		public virtual string GetErrorMessage()
		{
			return errorMessage;
		}
	}
}
