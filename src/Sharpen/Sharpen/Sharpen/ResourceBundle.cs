namespace Sharpen
{
	using System;
	using System.Collections.Generic;
	using System.Globalization;
	using System.IO;
	using System.Reflection;

	public class ResourceBundle
	{
		private CultureInfo culture;
		private Dictionary<string, string> strings = new Dictionary<string, string> ();

		public static ResourceBundle GetBundle (string bundleClass, CultureInfo culture)
		{
			Assembly asm = null;
			foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies ()) {
				if (a.GetType (bundleClass) != null) {
					asm = a;
					break;
				}
			}
			if (asm == null)
				throw new MissingResourceException ();
			Stream manifestResourceStream;
			manifestResourceStream = asm.GetManifestResourceStream (bundleClass + "_" + culture.ToString().Replace ('-','_') + ".properties");
			if (manifestResourceStream == null)
				manifestResourceStream = asm.GetManifestResourceStream (bundleClass + "_" + culture.TwoLetterISOLanguageName + ".properties");
			if (manifestResourceStream == null)
				manifestResourceStream = asm.GetManifestResourceStream (bundleClass + ".properties");
			if (manifestResourceStream != null) {
				ResourceBundle bundle = new ResourceBundle ();
				bundle.culture = culture;
				bundle.Load (manifestResourceStream);
				return bundle;
			} else
				throw new MissingResourceException ();
		}

		public CultureInfo GetLocale ()
		{
			return this.culture;
		}

		public string GetString (string fieldName)
		{
			string str;
			if (this.strings.TryGetValue (fieldName, out str)) {
				return str;
			}
			throw new MissingResourceException ();
		}

		private void Load (Stream s)
		{
			using (s) {
				string str;
				StreamReader reader = new StreamReader (s);
				while ((str = reader.ReadLine ()) != null) {
					int index = str.IndexOf ('=');
					if (index != -1) {
						string value = str.Substring(index + 1).Trim();
						while (value.EndsWith("\\")) {
							value = value.Substring(0, value.Length - 1).Replace(@"\n", Environment.NewLine).TrimStart('\\');
							string readLine = reader.ReadLine();
							if (readLine != null) value = value + readLine.Trim().Replace(@"\n", Environment.NewLine).TrimStart('\\');
						}
						this.strings[str.Substring(0, index).Trim()] = value;
					}
				}
			}
		}
	}
}
