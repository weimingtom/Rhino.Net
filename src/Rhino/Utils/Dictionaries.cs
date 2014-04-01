﻿using System.Collections.Generic;

namespace Rhino.Utils
{
	public static class Dictionaries
	{
		public static TValue GetValueOrDefault<TKey, TValue>(this IDictionary<TKey, TValue> d, TKey key)
		{
			TValue val;
			d.TryGetValue(key, out val);
			return val;
		}
	}
}