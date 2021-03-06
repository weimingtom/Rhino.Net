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
	public interface ConstProperties
	{
		// API class
		/// <summary>Sets a named const property in this object.</summary>
		/// <remarks>
		/// Sets a named const property in this object.
		/// <p>
		/// The property is specified by a string name
		/// as defined for <code>Scriptable.get</code>.
		/// <p>
		/// The possible values that may be passed in are as defined for
		/// <code>Scriptable.get</code>. A class that implements this method may choose
		/// to ignore calls to set certain properties, in which case those
		/// properties are effectively read-only.<p>
		/// For properties defined in a prototype chain,
		/// use <code>putProperty</code> in ScriptableObject. <p>
		/// Note that if a property <i>a</i> is defined in the prototype <i>p</i>
		/// of an object <i>o</i>, then evaluating <code>o.a = 23</code> will cause
		/// <code>set</code> to be called on the prototype <i>p</i> with
		/// <i>o</i> as the  <i>start</i> parameter.
		/// To preserve JavaScript semantics, it is the Scriptable
		/// object's responsibility to modify <i>o</i>. <p>
		/// This design allows properties to be defined in prototypes and implemented
		/// in terms of getters and setters of Java values without consuming slots
		/// in each instance.<p>
		/// <p>
		/// The values that may be set are limited to the following:
		/// <UL>
		/// <LI>java.lang.Boolean objects</LI>
		/// <LI>java.lang.String objects</LI>
		/// <LI>java.lang.Number objects</LI>
		/// <LI>Rhino.Scriptable objects</LI>
		/// <LI>null</LI>
		/// <LI>The value returned by Context.getUndefinedValue()</LI>
		/// </UL><p>
		/// Arbitrary Java objects may be wrapped in a Scriptable by first calling
		/// <code>Context.toObject</code>. This allows the property of a JavaScript
		/// object to contain an arbitrary Java object as a value.<p>
		/// Note that <code>has</code> will be called by the runtime first before
		/// <code>set</code> is called to determine in which object the
		/// property is defined.
		/// Note that this method is not expected to traverse the prototype chain,
		/// which is different from the ECMA [[Put]] operation.
		/// </remarks>
		/// <param name="name">the name of the property</param>
		/// <param name="start">the object whose property is being set</param>
		/// <param name="value">value to set the property to</param>
		/// <seealso cref="Scriptable.Has(string, SIScriptable">Scriptable.Has(string, Scriptable)</seealso>
		/// <seealso cref="Scriptable.Get(string, SIScriptable">Scriptable.Get(string, Scriptable)</seealso>
		/// <seealso cref="ScriptableObject.PutProperty(Scriptable, string, object)">ScriptableObject.PutProperty(Scriptable, string, object)</seealso>
		/// <seealso cref="Context.ToObject(object, Scriptable)">Context.ToObject(object, Scriptable)</seealso>
		void PutConst(string name, Scriptable start, object value);

		/// <summary>Reserves a definition spot for a const.</summary>
		/// <remarks>
		/// Reserves a definition spot for a const.  This will set up a definition
		/// of the const property, but set its value to undefined.  The semantics of
		/// the start parameter is the same as for putConst.
		/// </remarks>
		/// <param name="name">The name of the property.</param>
		/// <param name="start">The object whose property is being reserved.</param>
		void DefineConst(string name, Scriptable start);

		/// <summary>Returns true if the named property is defined as a const on this object.</summary>
		/// <remarks>Returns true if the named property is defined as a const on this object.</remarks>
		/// <param name="name"></param>
		/// <returns>
		/// true if the named property is defined as a const, false
		/// otherwise.
		/// </returns>
		bool IsConst(string name);
	}
}
