/*
 * This code is derived from rhino (http://github.com/mozilla/rhino)
 * 
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using System;
using System.IO;
using NUnit.Framework;
using Sharpen;

namespace Rhino.Tests
{
	[TestFixture]
	public sealed class Bug482203Test
	{
		/// <exception cref="System.Exception"></exception>
		[Test]
		public void TestJsApi()
		{
			Context cx = Context.Enter();
			try
			{
				cx.SetOptimizationLevel(-1);
				var manifestResourceStream = GetResourceAsStream();
				Script script = cx.CompileReader(new StreamReader(manifestResourceStream == null ? null : InputStream.Wrap(manifestResourceStream)), string.Empty, 1, null);
				Scriptable scope = cx.InitStandardObjects();
				script.Exec(cx, scope);
				int counter = 0;
				for (; ; )
				{
					object cont = ScriptableObject.GetProperty(scope, "c");
					if (cont == null)
					{
						break;
					}
					counter++;
					((Callable)cont).Call(cx, scope, scope, new object[] { null });
				}
				Assert.AreEqual(counter, 5);
				Assert.AreEqual(3, ScriptableObject.GetProperty(scope, "result"));
			}
			finally
			{
				Context.Exit();
			}
		}

		/// <exception cref="System.Exception"></exception>
		[Test]
		public void TestJavaApi()
		{
			Context cx = Context.Enter();
			try
			{
				cx.SetOptimizationLevel(-1);
				Script script = cx.CompileReader(new StreamReader(GetResourceAsStream()), string.Empty, 1, null);
				Scriptable scope = cx.InitStandardObjects();
				cx.ExecuteScriptWithContinuations(script, scope);
				int counter = 0;
				for (; ; )
				{
					object cont = ScriptableObject.GetProperty(scope, "c");
					if (cont == null)
					{
						break;
					}
					counter++;
					cx.ResumeContinuation(cont, scope, null);
				}
				Assert.AreEqual(counter, 5);
				Assert.AreEqual(3, ScriptableObject.GetProperty(scope, "result"));
			}
			finally
			{
				Context.Exit();
			}
		}

		private static Stream GetResourceAsStream()
		{
			var type = typeof (Bug482203Test);
			return type.Assembly.GetManifestResourceStream(type.Namespace + "." + "Bug482203.js");
		}
	}
}
