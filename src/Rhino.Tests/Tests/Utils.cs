/*
 * This code is derived from rhino (http://github.com/mozilla/rhino)
 * 
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using System;

namespace Rhino.Tests
{
	/// <summary>Misc utilities to make test code easier.</summary>
	/// <remarks>Misc utilities to make test code easier.</remarks>
	/// <author>Marc Guillemot</author>
	public static class Utils
	{
		/// <summary>Runs the action successively with all available optimization levels</summary>
		public static void RunWithAllOptimizationLevels(ContextAction action)
		{
			RunWithOptimizationLevel(action, -1);
			RunWithOptimizationLevel(action, 0);
			RunWithOptimizationLevel(action, 1);
		}

		/// <summary>Runs the action successively with all available optimization levels</summary>
		public static void RunWithAllOptimizationLevels(ContextFactory contextFactory, ContextAction action)
		{
			RunWithOptimizationLevel(contextFactory, action, -1);
			RunWithOptimizationLevel(contextFactory, action, 0);
			RunWithOptimizationLevel(contextFactory, action, 1);
		}

		/// <summary>Runs the provided action at the given optimization level</summary>
		public static void RunWithOptimizationLevel(ContextAction action, int optimizationLevel)
		{
			RunWithOptimizationLevel(new ContextFactory(), action, optimizationLevel);
		}

		/// <summary>Runs the provided action at the given optimization level</summary>
		public static void RunWithOptimizationLevel(ContextFactory contextFactory, ContextAction action, int optimizationLevel)
		{
			Context cx = contextFactory.EnterContext();
			try
			{
				cx.SetOptimizationLevel(optimizationLevel);
				action(cx);
			}
			finally
			{
				Context.Exit();
			}
		}

		/// <summary>Execute the provided script in a fresh context as "myScript.js".</summary>
		/// <remarks>Execute the provided script in a fresh context as "myScript.js".</remarks>
		/// <param name="script">the script code</param>
		/// <param name="optimizationLevel"></param>
		internal static void ExecuteScript(string script, int optimizationLevel)
		{
			RunWithOptimizationLevel(cx =>
			{
				Scriptable scope = cx.InitStandardObjects();
				return cx.EvaluateString(scope, script, "myScript.js", 1, null);
			}, optimizationLevel);
		}
	}
}
