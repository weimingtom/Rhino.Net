using System.Net.NetworkInformation;
using Rhino.Utils;
#if COMPILATION
using System.Linq;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using Org.Mozilla.Classfile;
using Rhino.Ast;
using Sharpen;
using Label = System.Reflection.Emit.Label;

namespace Rhino.Optimizer
{
	internal class BodyCodegen
	{
		public void GenerateMethodBody(MethodBuilder method)
		{
			// generate the body of the current function or script object
			var il = method.GetILGenerator();
			GeneratePrologue(il);
			Node treeTop = fnCurrent != null
				? scriptOrFn.GetLastChild()
				: scriptOrFn;
			GenerateStatement(il, treeTop);
			GenerateEpilogue(il);
		}

		// This creates a the user-facing function that returns a NativeGenerator object.
		public void GenerateGeneratorBody(MethodBuilder method)
		{
			var il = method.GetILGenerator();
			argsArgument = firstFreeLocal++;
			localsMax = firstFreeLocal;
			// get top level scope
			if (fnCurrent != null)
			{
				// Unless we're in a direct call use the enclosing scope
				// of the function as our variable object.
				il.Emit(OpCodes.Ldarg_0);
				il.Emit(OpCodes.Callvirt, typeof (Scriptable).GetMethod("get_ParentScope", Type.EmptyTypes));
				il.EmitStoreArgument(2);
			}
			// generators are forced to have an activation record
			il.Emit(OpCodes.Ldarg_0);
			il.Emit(OpCodes.Ldarg_2);
			il.EmitLoadArgument(argsArgument);
			AddScriptRuntimeInvoke(il, "CreateFunctionActivation", typeof (NativeFunction), typeof (Scriptable), typeof (Object[]));
			il.EmitStoreArgument(2);
		   
			// create a function object
			// Call function constructor
			il.Emit(OpCodes.Ldarg_2); // scriptable
			il.Emit(OpCodes.Ldarg_1); // load 'cx'
			il.EmitLoadConstant(scriptOrFnIndex);
			il.Emit(OpCodes.Newobj, constructor);
			GenerateNestedFunctionInits(il);
			
			// create the NativeGenerator object that we return
			il.Emit(OpCodes.Ldarg_2);
			il.Emit(OpCodes.Ldarg_3);
			il.EmitLoadConstant(maxLocals);
			il.EmitLoadConstant(maxStack);
			AddOptRuntimeInvoke(il, "CreateNativeGenerator", new[] {typeof (NativeFunction), typeof (Scriptable), typeof (Scriptable), typeof (int), typeof (int)});
			il.Emit(OpCodes.Ret);
		}

		private void GenerateNestedFunctionInits(ILGenerator il)
		{
			var functionCount = scriptOrFn.GetFunctionCount();
			for (var i = 0; i != functionCount; i++)
			{
				var ofn = OptFunctionNode.Get(scriptOrFn, i);
				if (ofn.fnode.GetFunctionType() == FunctionNode.FUNCTION_STATEMENT)
				{
					VisitFunction(il, ofn, FunctionNode.FUNCTION_STATEMENT);
				}
			}
		}

		private void InitBodyGeneration()
		{
			varRegisters = null;
			if (scriptOrFn.GetType() == Token.FUNCTION)
			{
				fnCurrent = OptFunctionNode.Get(scriptOrFn);
				hasVarsInRegs = !fnCurrent.fnode.RequiresActivation();
				if (hasVarsInRegs)
				{
					var n = fnCurrent.fnode.GetParamAndVarCount();
					if (n != 0)
					{
						varRegisters = new IVariableInfoEmitter[n];
					}
				}
				inDirectCallFunction = fnCurrent.IsTargetOfDirectCall();
				if (inDirectCallFunction && !hasVarsInRegs)
				{
					Codegen.BadTree();
				}
			}
			else
			{
				fnCurrent = null;
				hasVarsInRegs = false;
				inDirectCallFunction = false;
			}
			locals = new int[MAX_LOCALS];
			thisObjLocal = 3;
			localsMax = 4;
			// number of parms + "this"
			firstFreeLocal = 4;
			popvLocal = null;
			argsArgument = -1;
			itsZeroArgArray = null;
			itsOneArgArray = null;
			epilogueLabel = null;
			enterAreaStartLabel = null;
		}

		/// <summary>Generate the prologue for a function or script.</summary>
		/// <param name="il"></param>
		/// <remarks>Generate the prologue for a function or script.</remarks>
		private void GeneratePrologue(ILGenerator il)
		{
			if (inDirectCallFunction)
			{
				var directParameterCount = scriptOrFn.GetParamCount();
				// 0 is reserved for function Object 'this'
				// 1 is reserved for context
				// 2 is reserved for parentScope
				// 3 is reserved for script 'this'
				for (var i = 0; i < directParameterCount; i++)
				{
					varRegisters[i] = new ArgumentInfoEmitter(firstFreeLocal, firstFreeLocal + 1);
					// 2 is 1 for Object parm and 2 for double parm
					firstFreeLocal += 2;
				}
				if (!fnCurrent.GetParameterNumberContext())
				{
					// make sure that all parameters are objects
					itsForcedObjectParameters = true;
					for (var i = 0; i < directParameterCount; i++)
					{
						var reg = varRegisters[i];
						reg.EmitLoadSlot1(il);
						il.Emit(OpCodes.Ldtoken, typeof (void));
						il.Emit(OpCodes.Ceq);
						var isObjectLabel = il.DefineLabel();
						il.Emit(OpCodes.Brfalse, isObjectLabel);
						reg.EmitLoadSlot2(il);
						reg.EmitStoreSlot1(il);
						il.MarkLabel(isObjectLabel);
					}
				}
			}
			if (fnCurrent != null)
			{
				// Use the enclosing scope of the function as our variable object.
				il.Emit(OpCodes.Ldarg_0);
				il.Emit(OpCodes.Callvirt, typeof (Scriptable).GetMethod("get_ParentScope", Type.EmptyTypes));
				il.EmitStoreArgument(2);
			}
			// reserve 'args[]'
			argsArgument = firstFreeLocal++;
			localsMax = firstFreeLocal;
			// Generate Generator specific prelude
			if (isGenerator)
			{
				// reserve 'args[]'
				operationLocal = firstFreeLocal++;
				localsMax = firstFreeLocal;
				// Local 3 is a reference to a GeneratorState object. The rest
				// of codegen expects local 3 to be a reference to the thisObj.
				// So move the value in local 3 to generatorStateLocal, and load
				// the saved thisObj from the GeneratorState object.
				il.Emit(OpCodes.Ldarg_3);
				generatorStateLocal = il.DeclareLocal(typeof (OptRuntime.GeneratorState));
				localsMax = firstFreeLocal;
				il.Emit(OpCodes.Castclass, OptRuntime.GeneratorState.CLASS_NAME);
				il.Emit(OpCodes.Dup);
				il.EmitStoreLocal(generatorStateLocal);
				il.Emit(OpCodes.Ldfld, OptRuntime.GeneratorState.CLASS_NAME.GetField(OptRuntime.GeneratorState.thisObj_NAME));
				il.EmitStoreArgument(3);
				if (epilogueLabel == null)
				{
					epilogueLabel = il.DefineLabel();
				}
				var targets = ((FunctionNode)scriptOrFn).GetResumptionPoints();
				if (targets != null)
				{
					// get resumption point
					GenerateGetGeneratorResumptionPoint(il);
					// generate dispatch table
					generatorSwitch = il.DefineSwitchTable(targets.Count + GENERATOR_START + 1);
					il.Emit(OpCodes.Switch, generatorSwitch);
					GenerateCheckForThrowOrClose(il, null, false, GENERATOR_START);
				}
			}
			// Compile RegExp literals if this is a script. For functions
			// this is performed during instantiation in functionInit
			if (fnCurrent == null && scriptOrFn.GetRegExpCount() != 0)
			{
				il.Emit(OpCodes.Ldarg_1);
				il.Emit(OpCodes.Call, regExpInit);
			}
			if (compilerEnv.GenerateObserverCount)
			{
				SaveCurrentCodeOffset(il);
			}
			if (hasVarsInRegs)
			{
				// No need to create activation. Pad arguments if need be.
				var parmCount = scriptOrFn.GetParamCount();
				if (parmCount > 0 && !inDirectCallFunction)
				{
					// Set up args array
					// check length of arguments, pad if need be
					il.EmitLoadArgument(argsArgument);
					il.Emit(OpCodes.Ldlen);
					il.EmitLoadConstant(parmCount);
					var label = il.DefineLabel();
					il.Emit(OpCodes.Clt);
					il.Emit(OpCodes.Brfalse, label);
					il.EmitLoadArgument(argsArgument);
					il.EmitLoadConstant(parmCount);
					AddScriptRuntimeInvoke(il, "PadArguments", typeof(object[]), typeof(int));
					il.EmitStoreArgument(argsArgument);
					il.MarkLabel(label);
				}
				var paramCount = fnCurrent.fnode.GetParamCount();
				var varCount = fnCurrent.fnode.GetParamAndVarCount();
				var constDeclarations = fnCurrent.fnode.GetParamAndVarConst();
				// REMIND - only need to initialize the vars that don't get a value
				// before the next call and are used in the function
				IVariableInfoEmitter firstUndefVar = null;
				for (var i = 0; i < varCount; i++)
				{
					IVariableInfoEmitter reg = null;
					if (i < paramCount)
					{
						if (!inDirectCallFunction)
						{
							/*
							 * var x_i = args[i];
							 */
							reg = new LocalInfoEmitter(il.DeclareLocal(typeof (object)), null);
							il.EmitLoadArgument(argsArgument);
							il.EmitLoadConstant(i);
							il.Emit(OpCodes.Ldelem_Ref);
							reg.EmitStoreSlot1(il);
						}
					}
					else
					{
						if (fnCurrent.IsNumberVar(i))
						{
							/*
							 * var x_i = 0.0;
							 */
							reg = new LocalInfoEmitter(
								il.DeclareLocal(typeof (double)),
								constDeclarations[i] ? il.DeclareLocal(typeof (int)) : null);
							il.EmitLoadConstant(0.0);
							reg.EmitStoreSlot1(il);
						}
						else
						{
							/*
							 * var x_i = Undefined.instance;
							 * 
							 * OR
							 * 
							 * var x_i = firstUndefVar; //TODO do we need this optimization?
							 */
							reg = new LocalInfoEmitter(
								il.DeclareLocal(typeof(object)),
								constDeclarations[i] ? il.DeclareLocal(typeof(int)) : null);

							if (firstUndefVar == null)
							{
								Codegen.PushUndefined(il);
								firstUndefVar = reg;
							}
							else
							{
								firstUndefVar.EmitLoadSlot1(il);
							}
							reg.EmitStoreSlot1(il);
						}
					}
					if (reg != null)
					{
						if (constDeclarations[i])
						{
							/*
							 * var x_const = 0;
							 */ 
							il.EmitLoadConstant(0);
							reg.EmitStoreSlot2(il);
						}
						varRegisters[i] = reg;
					}
					// Add debug table entry if we're generating debug info
					if (compilerEnv.GenerateDebugInfo)
					{
						var name = fnCurrent.fnode.GetParamOrVarName(i);
						Type type = fnCurrent.IsNumberVar(i) ? typeof (double) : typeof (Object);
						var startPC = il.ILOffset;
						if (reg == null)
						{
							reg = varRegisters[i];
						}
						reg.SetLocalInfo(name);
					}
				}
				// Skip creating activation object.
				return;
			}
			// skip creating activation object for the body of a generator. The
			// activation record required by a generator has already been created
			// in generateGenerator().
			if (isGenerator)
			{
				return;
			}
			if (fnCurrent != null)
			{
				il.Emit(OpCodes.Ldarg_0);
				il.Emit(OpCodes.Ldarg_2);
				il.EmitLoadArgument(argsArgument);
				AddScriptRuntimeInvoke(il, "CreateFunctionActivation", new[] { typeof (NativeFunction), typeof (Scriptable), typeof (Object[]) });
				il.EmitStoreArgument(2);
				il.Emit(OpCodes.Ldarg_1);
				il.Emit(OpCodes.Ldarg_2);
				AddScriptRuntimeInvoke(il, "EnterActivationFunction", new[] { typeof (Context), typeof (Scriptable) });
				il.BeginExceptionBlock();
			}
			else
			{
				il.Emit(OpCodes.Ldarg_0);
				il.Emit(OpCodes.Ldarg_3);
				il.Emit(OpCodes.Ldarg_1);
				il.Emit(OpCodes.Ldarg_2);
				il.EmitLoadConstant(0); // false to indicate it is not eval script
				AddScriptRuntimeInvoke(il, "InitScript", new[] { typeof (NativeFunction), typeof (Scriptable), typeof (Context), typeof (Scriptable), typeof (bool) });
			}
			enterAreaStartLabel = il.DefineLabel();
			epilogueLabel = il.DefineLabel();
			il.MarkLabel(enterAreaStartLabel.Value);
			GenerateNestedFunctionInits(il);
			if (fnCurrent == null)
			{
				// OPT: use dataflow to prove that this assignment is dead
				popvLocal = il.DeclareLocal(typeof (object));
				Codegen.PushUndefined(il);
				il.EmitStoreLocal(popvLocal);
				var linenum = scriptOrFn.GetEndLineno();
				if (linenum != -1)
				{
					AddLineNumberEntry(il, linenum);
				}
			}
			else
			{
				if (fnCurrent.itsContainsCalls0)
				{
					itsZeroArgArray = il.DeclareLocal(typeof (Object[]));
					il.Emit(OpCodes.Ldsfld, typeof(ScriptRuntime).GetField("emptyArgs"));
					il.EmitStoreLocal(itsZeroArgArray);
				}
				if (fnCurrent.itsContainsCalls1)
				{
					itsOneArgArray = il.DeclareLocal(typeof (object[]));
					il.EmitLoadConstant(1);
					il.Emit(OpCodes.Newarr, typeof (Object));
					il.Emit(OpCodes.Stloc, itsOneArgArray);
				}
			}
		}

		private void AddLineNumberEntry(ILGenerator il, int linenum)
		{
		}

		private void GenerateGetGeneratorResumptionPoint(ILGenerator il)
		{
			il.EmitLoadLocal(generatorStateLocal);
			il.Emit(OpCodes.Ldfld, OptRuntime.GeneratorState.CLASS_NAME.GetField(OptRuntime.GeneratorState.resumptionPoint_NAME));
		}

		private void GenerateSetGeneratorResumptionPoint(ILGenerator il, int nextState)
		{
			il.EmitLoadLocal(generatorStateLocal);
			il.EmitLoadConstant(nextState);
			il.Emit(OpCodes.Stfld, OptRuntime.GeneratorState.CLASS_NAME.GetField(OptRuntime.GeneratorState.resumptionPoint_NAME));
		}

		private void GenerateGetGeneratorStackState(ILGenerator il)
		{
			il.EmitLoadLocal(generatorStateLocal);
			AddOptRuntimeInvoke(il, "GetGeneratorStackState", new[] {typeof (object)});
		}

		private void GenerateEpilogue(ILGenerator il)
		{
			if (compilerEnv.GenerateObserverCount)
			{
				AddInstructionCount(il);
			}
			if (isGenerator)
			{
				// generate locals initialization
				var liveLocals = ((FunctionNode)scriptOrFn).GetLiveLocals();
				if (liveLocals != null)
				{
					var nodes = ((FunctionNode)scriptOrFn).GetResumptionPoints();
					foreach (var node in nodes)
					{
						var live = liveLocals.GetValueOrDefault(node);
						if (live != null)
						{
							il.MarkLabel(generatorSwitch [GetNextGeneratorState(node)]);
							GenerateGetGeneratorLocalsState(il);
							for (var j = 0; j < live.Length; j++)
							{
								il.Emit(OpCodes.Dup);
								il.EmitLoadConstant(j);
								il.Emit(OpCodes.Ldelem_Ref);
								il.EmitStoreLocal(live[j]);
							}
							il.Emit(OpCodes.Pop);
							il.Emit(OpCodes.Br, GetTargetLabel(il, node));
						}
					}
				}
				// generate dispatch tables for finally
				if (finallys != null)
				{
					foreach (var n in finallys.Keys)
					{
						if (n.GetType() == Token.FINALLY)
						{
							var ret = finallys.GetValueOrDefault(n);
							// the finally will jump here
							il.MarkLabel(ret.tableLabel);
							//itsStackTop = stackTop;
							// generate a dispatch table
							il.Emit(OpCodes.Switch, ret.jsrPoints.ToArray());
						}
					}
				}
			}
			if (epilogueLabel != null)
			{
				il.MarkLabel(epilogueLabel.Value);
			}
			if (hasVarsInRegs)
			{
				il.Emit(OpCodes.Ret);
				return;
			}
		    if (isGenerator)
		    {
		        if (((FunctionNode) scriptOrFn).GetResumptionPoints() != null)
		        {
		            //cfw.markTableSwitchDefault(generatorSwitch);
		            //no actions required.
		        }
		        // change state for re-entry
		        GenerateSetGeneratorResumptionPoint(il, GENERATOR_TERMINATE);
		        // throw StopIteration
		        il.Emit(OpCodes.Ldarg_2);
		        AddOptRuntimeInvoke(il, "ThrowStopIteration", new[] { typeof (object) });
		        Codegen.PushUndefined(il);
		        il.Emit(OpCodes.Ret);
		    }
		    else if (fnCurrent == null)
		    {
		        il.EmitLoadLocal(popvLocal);
		        il.Emit(OpCodes.Ret);
		    }
		    else
		    {
		        var exceptionObject = il.DeclareLocal(typeof (object));
		        il.EmitStoreLocal(exceptionObject);
		        il.BeginFinallyBlock();
                GenerateActivationExit(il);
		        il.EndExceptionBlock();
		        il.EmitLoadLocal(exceptionObject);
		        il.Emit(OpCodes.Ret);
		    }
		}

		// catch any
		private void GenerateGetGeneratorLocalsState(ILGenerator il)
		{
			il.EmitLoadLocal(generatorStateLocal);
			AddOptRuntimeInvoke(il, "GetGeneratorLocalsState", new[] {typeof (object)});
		}

		private void GenerateActivationExit(ILGenerator il)
		{
			if (fnCurrent == null || hasVarsInRegs)
			{
				throw Kit.CodeBug();
			}
			il.Emit(OpCodes.Ldarg_1);
			AddScriptRuntimeInvoke(il, "ExitActivationFunction", new[] { typeof (Context) });
		}

		private void GenerateStatement(ILGenerator il, Node node)
		{
			UpdateLineNumber(node, il);
			var type = node.GetType();
			var child = node.GetFirstChild();
			switch (type)
			{
				case Token.LOOP:
				case Token.LABEL:
				case Token.WITH:
				case Token.SCRIPT:
				case Token.BLOCK:
				case Token.EMPTY:
				{
					// no-ops.
					if (compilerEnv.GenerateObserverCount)
					{
						// Need to add instruction count even for no-ops to catch
						// cases like while (1) {}
						AddInstructionCount(il, 1);
					}
					while (child != null)
					{
						GenerateStatement(il, child);
						child = child.GetNext();
					}
					break;
				}

				case Token.LOCAL_BLOCK:
				{
					var prevLocal = inLocalBlock;
					inLocalBlock = true;
				    var local = DeclareLocal(il);
					if (isGenerator)
					{
						il.Emit(OpCodes.Ldnull);
						il.EmitStoreLocal(local);
					}
					node.PutProp(Node.LOCAL_PROP, local);
					while (child != null)
					{
						GenerateStatement(il, child);
						child = child.GetNext();
					}
				    ReleaseLocal(local);
					node.RemoveProp(Node.LOCAL_PROP);
					inLocalBlock = prevLocal;
					break;
				}

				case Token.FUNCTION:
				{
					var fnIndex = node.GetExistingIntProp(Node.FUNCTION_PROP);
					var ofn = OptFunctionNode.Get(scriptOrFn, fnIndex);
					var t = ofn.fnode.GetFunctionType();
					if (t == FunctionNode.FUNCTION_EXPRESSION_STATEMENT)
					{
						VisitFunction(il, ofn, t);
					}
					else
					{
						if (t != FunctionNode.FUNCTION_STATEMENT)
						{
							throw Codegen.BadTree();
						}
					}
					break;
				}

				case Token.TRY:
				{
					VisitTryCatchFinally(il, (Jump)node, child);
					break;
				}

				case Token.CATCH_SCOPE:
				{
					// nothing stays on the stack on entry into a catch scope
					//cfw.SetStackTop(0);
					var local = GetLocalBlockRegister(node);
					var scopeIndex = node.GetExistingIntProp(Node.CATCH_SCOPE_PROP);
					var name = child.GetString();
					// name of exception
					child = child.GetNext();
					GenerateExpression(il, child, node);
					// load expression object
					if (scopeIndex == 0)
					{
						il.Emit(OpCodes.Ldnull);
					}
					else
					{
						// Load previous catch scope object
						il.EmitLoadLocal(local);
					}
					il.EmitLoadConstant(name);
					il.Emit(OpCodes.Ldarg_1);
					il.Emit(OpCodes.Ldarg_2);
					AddScriptRuntimeInvoke(il, "NewCatchScope", new[] { typeof (Exception), typeof (Scriptable), typeof (String), typeof (Context), typeof (Scriptable) });
					il.EmitStoreLocal(local);
					break;
				}

				case Token.THROW:
				{
					GenerateExpression(il, child, node);
					if (compilerEnv.GenerateObserverCount)
					{
						AddInstructionCount(il);
					}
					GenerateThrowJavaScriptException(il);
					break;
				}

				case Token.RETHROW:
				{
					if (compilerEnv.GenerateObserverCount)
					{
						AddInstructionCount(il);
					}
					var local = GetLocalBlockRegister(node);
					il.EmitLoadLocal(local);
					il.Emit(OpCodes.Throw);
					break;
				}

				case Token.RETURN_RESULT:
				case Token.RETURN:
				{
					if (!isGenerator)
					{
						if (child != null)
						{
							GenerateExpression(il, child, node);
						}
						else
						{
							if (type == Token.RETURN)
							{
								Codegen.PushUndefined(il);
							}
							else
							{
								if (popvLocal == null)
								{
									throw Codegen.BadTree();
								}
								il.EmitLoadLocal(popvLocal);
							}
						}
					}
					if (compilerEnv.GenerateObserverCount)
					{
						AddInstructionCount(il);
					}
					if (epilogueLabel == null)
					{
						if (!hasVarsInRegs)
						{
							throw Codegen.BadTree();
						}
						epilogueLabel = il.DefineLabel();
					}
					il.Emit(OpCodes.Br, epilogueLabel.Value);
					break;
				}

				case Token.SWITCH:
				{
					if (compilerEnv.GenerateObserverCount)
					{
						AddInstructionCount(il);
					}
					VisitSwitch(il, (Jump)node, child);
					break;
				}

				case Token.ENTERWITH:
				{
					GenerateExpression(il, child, node);
					il.Emit(OpCodes.Ldarg_1);
					il.Emit(OpCodes.Ldarg_2);
					AddScriptRuntimeInvoke(il, "EnterWith", new[] { typeof (Object), typeof (Context), typeof (Scriptable) });
					il.EmitStoreArgument(2);
					IncReferenceWordLocal(2);
					break;
				}

				case Token.LEAVEWITH:
				{
					il.Emit(OpCodes.Ldarg_2);
					AddScriptRuntimeInvoke(il, "LeaveWith", new[] { typeof (Scriptable) });
					il.EmitStoreArgument(2);
					DecReferenceWordLocal(2);
					break;
				}

				case Token.ENUM_INIT_KEYS:
				case Token.ENUM_INIT_VALUES:
				case Token.ENUM_INIT_ARRAY:
				{
					GenerateExpression(il, child, node);
					il.Emit(OpCodes.Ldarg_1);
					var enumType = type == Token.ENUM_INIT_KEYS ? ScriptRuntime.ENUMERATE_KEYS : type == Token.ENUM_INIT_VALUES ? ScriptRuntime.ENUMERATE_VALUES : ScriptRuntime.ENUMERATE_ARRAY;
					il.EmitLoadConstant(enumType);
					AddScriptRuntimeInvoke(il, "EnumInit", new[] { typeof (Object), typeof (Context), typeof (int) });
					var local = GetLocalBlockRegister(node);
					il.EmitStoreLocal(local);
					break;
				}

				case Token.EXPR_VOID:
				{
					if (child.GetType() == Token.SETVAR)
					{
						VisitSetVar(il, child, child.GetFirstChild(), false);
					}
					else
					{
						if (child.GetType() == Token.SETCONSTVAR)
						{
							VisitSetConstVar(il, child, child.GetFirstChild(), false);
						}
						else
						{
							if (child.GetType() == Token.YIELD)
							{
								GenerateYieldPoint(il, child, false);
							}
							else
							{
								GenerateExpression(il, child, node);
								il.Emit(OpCodes.Pop);
							}
						}
					}
					break;
				}

				case Token.EXPR_RESULT:
				{
					GenerateExpression(il, child, node);
					if (popvLocal == null)
					{
						popvLocal = il.DeclareLocal(typeof (object));
					}
					il.EmitStoreLocal(popvLocal);
					break;
				}

				case Token.TARGET:
				{
					if (compilerEnv.GenerateObserverCount)
					{
						AddInstructionCount(il);
					}
					var label = GetTargetLabel(il, node);
					il.MarkLabel(label);
					if (compilerEnv.GenerateObserverCount)
					{
						SaveCurrentCodeOffset(il);
					}
					break;
				}

				case Token.JSR:
				case Token.GOTO:
				case Token.IFEQ:
				case Token.IFNE:
				{
					if (compilerEnv.GenerateObserverCount)
					{
						AddInstructionCount(il);
					}
					VisitGoto(il, (Jump)node, type, child);
					break;
				}

				case Token.FINALLY:
				{
					// This is the non-exception case for a finally block. In
					// other words, since we inline finally blocks wherever
					// jsr was previously used, and jsr is only used when the
					// function is not a generator, we don't need to generate
					// this case if the function isn't a generator.
					if (!isGenerator)
					{
						break;
					}
					if (compilerEnv.GenerateObserverCount)
					{
						SaveCurrentCodeOffset(il);
					}
					// there is exactly one value on the stack when enterring
					// finally blocks: the return address (or its int encoding)
					//cfw.SetStackTop(1);
					// Save return address in a new local
					LocalBuilder finallyRegister = DeclareLocal(il);
                    //il.BeginFinallyBlock();
					il.EmitStoreLocal(finallyRegister);
					while (child != null)
					{
						GenerateStatement(il, child);
						child = child.GetNext();
					}
					il.EmitLoadLocal(finallyRegister);
					il.Emit(OpCodes.Conv_I4);
					var ret = finallys.GetValueOrDefault(node);
					ret.tableLabel = il.DefineLabel();
					il.Emit(OpCodes.Leave, ret.tableLabel);
				    //il.Emit(OpCodes.Endfinally);
					break;
				}

				case Token.DEBUGGER:
				{
					break;
				}

				default:
				{
					throw Codegen.BadTree();
				}
			}
		}

		private void GenerateThrowJavaScriptException(ILGenerator il)
		{
			il.EmitLoadConstant(scriptOrFn.GetSourceName());
			il.EmitLoadConstant(itsLineNumber);
			il.Emit(OpCodes.Newobj, typeof (JavaScriptException).GetConstructor(new[] { typeof (object), typeof (string), typeof (int) }));
			il.Emit(OpCodes.Throw);
		}

		private int GetNextGeneratorState(Node node)
		{
			var nodeIndex = ((FunctionNode)scriptOrFn).GetResumptionPoints().IndexOf(node);
			return nodeIndex + GENERATOR_YIELD_START;
		}

		private void GenerateExpression(ILGenerator il, Node node, Node parent)
		{
			var type = node.GetType();
			var child = node.GetFirstChild();
			switch (type)
			{
				case Token.USE_STACK:
				{
					break;
				}

				case Token.FUNCTION:
				{
					if (fnCurrent != null || parent.GetType() != Token.SCRIPT)
					{
						var fnIndex = node.GetExistingIntProp(Node.FUNCTION_PROP);
						var ofn = OptFunctionNode.Get(scriptOrFn, fnIndex);
						var t = ofn.fnode.GetFunctionType();
						if (t != FunctionNode.FUNCTION_EXPRESSION)
						{
							throw Codegen.BadTree();
						}
						VisitFunction(il, ofn, t);
					}
					break;
				}

				case Token.NAME:
				{
					il.Emit(OpCodes.Ldarg_1);
					il.Emit(OpCodes.Ldarg_2);
					string k = node.GetString();
					il.EmitLoadConstant(k);
					AddScriptRuntimeInvoke(il, "Name", new[] { typeof (Context), typeof (Scriptable), typeof (String) });
					break;
				}

				case Token.CALL:
				case Token.NEW:
				{
					var specialType = node.GetIntProp(Node.SPECIALCALL_PROP, Node.NON_SPECIALCALL);
					if (specialType == Node.NON_SPECIALCALL)
					{
						var target = (OptFunctionNode)node.GetProp(Node.DIRECTCALL_PROP);
						if (target != null)
						{
							VisitOptimizedCall(il, node, target, type, child);
						}
						else
						{
							if (type == Token.CALL)
							{
								VisitStandardCall(il, node, child);
							}
							else
							{
								VisitStandardNew(il, node, child);
							}
						}
					}
					else
					{
						VisitSpecialCall(il, node, type, specialType, child);
					}
					break;
				}

				case Token.REF_CALL:
				{
					GenerateFunctionAndThisObj(il, child, node);
					// stack: ... functionObj thisObj
					child = child.GetNext();
					GenerateCallArgArray(il, node, child, false);
					il.Emit(OpCodes.Ldarg_1);
					AddScriptRuntimeInvoke(il, "CallRef", new[] { typeof (Callable), typeof (Scriptable), typeof (Object[]), typeof (Context) });
					break;
				}

				case Token.NUMBER:
				{
					var num = node.GetDouble();
					if (node.GetIntProp(Node.ISNUMBER_PROP, -1) != -1)
					{
						il.EmitLoadConstant(num);
					}
					else
					{
						Codegen.PushNumberAsObject(il, cfw, num);
					}
					break;
				}

				case Token.STRING:
				{
					string s = node.GetString();
					il.EmitLoadConstant(s);
					break;
				}

				case Token.THIS:
				{
					il.Emit(OpCodes.Ldarg_3);
					break;
				}

				case Token.THISFN:
				{
					il.Emit(OpCodes.Ldloc_0);
					break;
				}

				case Token.NULL:
				{
					il.Emit(OpCodes.Ldnull);
					break;
				}

				case Token.TRUE:
				{
					il.EmitLoadConstant(true);
					il.Emit(OpCodes.Box, typeof (bool));
					break;
				}

				case Token.FALSE:
				{
					il.EmitLoadConstant(false);
					il.Emit(OpCodes.Box, typeof(bool));
					break;
				}

				case Token.REGEXP:
				{
					// Create a new wrapper around precompiled regexp
					il.Emit(OpCodes.Ldarg_1);
					il.Emit(OpCodes.Ldarg_2);
					il.Emit(OpCodes.Ldsfld, tb.GetField(codegen.GetCompiledRegExpName(scriptOrFn, node.GetExistingIntProp(Node.REGEXP_PROP))));
					AddScriptRuntimeInvoke(il, "WrapRegExp", typeof (Context), typeof (Scriptable), typeof (Object));
					break;
				}

				case Token.COMMA:
				{
					var next = child.GetNext();
					while (next != null)
					{
						GenerateExpression(il, child, node);
						il.Emit(OpCodes.Pop);
						child = next;
						next = next.GetNext();
					}
					GenerateExpression(il, child, node);
					break;
				}

				case Token.ENUM_NEXT:
				case Token.ENUM_ID:
				{
					var local = GetLocalBlockRegister(node);
					il.EmitLoadLocal(local);
					if (type == Token.ENUM_NEXT)
					{
						AddScriptRuntimeInvoke(il, "EnumNext", new[] { typeof (Object) });
						il.Emit(OpCodes.Box, typeof (bool));
					}
					else
					{
						il.Emit(OpCodes.Ldarg_1);
						AddScriptRuntimeInvoke(il, "EnumId", new[] { typeof (Object), typeof (Context) });
					}
					break;
				}

				case Token.ARRAYLIT:
				{
					VisitArrayLiteral(il, node, child, false);
					break;
				}

				case Token.OBJECTLIT:
				{
					VisitObjectLiteral(il, node, child, false);
					break;
				}

				case Token.NOT:
				{
					var trueTarget = il.DefineLabel();
					var falseTarget = il.DefineLabel();
					var beyond = il.DefineLabel();
					GenerateIfJump(il, child, node, trueTarget, falseTarget);
					il.MarkLabel(trueTarget);
					il.EmitLoadConstant(false);
					il.Emit(OpCodes.Box, typeof(bool));
					il.Emit(OpCodes.Br, beyond);
					il.MarkLabel(falseTarget);
					il.EmitLoadConstant(true);
					il.Emit(OpCodes.Box, typeof(bool));
					il.MarkLabel(beyond);
					break;
				}

				case Token.BITNOT:
				{
					GenerateExpression(il, child, node);
					AddScriptRuntimeInvoke(il, "ToInt32", new[] { typeof (object) });
					il.EmitLoadConstant(-1);
					// implement ~a as (a ^ -1)
					il.Emit(OpCodes.Xor);
					il.Emit(OpCodes.Conv_R8);
					break;
				}

				case Token.VOID:
				{
					GenerateExpression(il, child, node);
					il.Emit(OpCodes.Pop);
					Codegen.PushUndefined(il);
					break;
				}

				case Token.TYPEOF:
				{
					GenerateExpression(il, child, node);
					AddScriptRuntimeInvoke(il, "TypeOf", new[] { typeof (object) });
					break;
				}

				case Token.TYPEOFNAME:
				{
					VisitTypeOfName(il, node);
					break;
				}

				case Token.INC:
				case Token.DEC:
				{
					VisitIncDec(il, node);
					break;
				}

				case Token.OR:
				case Token.AND:
				{
					GenerateExpression(il, child, node);
					il.Emit(OpCodes.Dup);
					AddScriptRuntimeInvoke(il, "ToBoolean", typeof (Object));
					var falseTarget = il.DefineLabel();
					il.Emit(type == Token.AND ? OpCodes.Brfalse : OpCodes.Brtrue, falseTarget);
					il.Emit(OpCodes.Pop);
					GenerateExpression(il, child.GetNext(), node);
					il.MarkLabel(falseTarget);
					break;
				}

				case Token.HOOK:
				{
					var ifThen = child.GetNext();
					var ifElse = ifThen.GetNext();
					GenerateExpression(il, child, node);
					AddScriptRuntimeInvoke(il, "ToBoolean", typeof (Object));
					var elseTarget = il.DefineLabel();
					il.Emit(OpCodes.Brfalse, elseTarget);
					GenerateExpression(il, ifThen, node);
					var afterHook = il.DefineLabel();
					il.Emit(OpCodes.Br, afterHook);
					il.MarkLabel(elseTarget);
					//itsStackTop = stackTop;
					GenerateExpression(il, ifElse, node);
					il.MarkLabel(afterHook);
					break;
				}

				case Token.ADD:
				{
					GenerateExpression(il, child, node);
					GenerateExpression(il, child.GetNext(), node);
					switch (node.GetIntProp(Node.ISNUMBER_PROP, -1))
					{
						case Node.BOTH:
						{
							il.Emit(OpCodes.Add);
							break;
						}

						case Node.LEFT:
						{
							AddOptRuntimeInvoke(il, "Add", new[] { typeof (double), typeof (object) });
							break;
						}

						case Node.RIGHT:
						{
							AddOptRuntimeInvoke(il, "Add", new[] { typeof(object), typeof(double) });
							break;
						}

						default:
						{
							if (child.GetType() == Token.STRING)
							{
								AddScriptRuntimeInvoke(il, "Add", new[] { typeof (string), typeof (Object) });
							}
							else
							{
								if (child.GetNext().GetType() == Token.STRING)
								{
									AddScriptRuntimeInvoke(il, "Add", new[] { typeof (Object), typeof (string) });
								}
								else
								{
									il.Emit(OpCodes.Ldarg_1);
									AddScriptRuntimeInvoke(il, "Add", new[] { typeof (Object), typeof (Object), typeof (Context) });
								}
							}
							break;
						}
					}
					break;
				}

				case Token.MUL:
				{
					VisitArithmetic(node, OpCodes.Mul, child, parent, il);
					break;
				}

				case Token.SUB:
				{
					VisitArithmetic(node, OpCodes.Sub, child, parent, il);
					break;
				}

				case Token.DIV:
				case Token.MOD:
				{
					VisitArithmetic(node, type == Token.DIV ? OpCodes.Div : OpCodes.Rem, child, parent, il);
					break;
				}

				case Token.BITOR:
				case Token.BITXOR:
				case Token.BITAND:
				case Token.LSH:
				case Token.RSH:
				case Token.URSH:
				{
					VisitBitOp(il, node, type, child);
					break;
				}

				case Token.POS:
				case Token.NEG:
				{
					GenerateExpression(il, child, node);
					AddObjectToNumberUnBoxed(il);
					if (type == Token.NEG)
					{
						il.Emit(OpCodes.Neg);
					}
					il.Emit(OpCodes.Box, typeof(double));
					break;
				}

				case Token.TO_DOUBLE:
				{
					// cnvt to double (not Double)
					GenerateExpression(il, child, node);
					AddObjectToNumber(il);
					break;
				}

				case Token.TO_OBJECT:
				{
					// convert from double
					var prop = -1;
					if (child.GetType() == Token.NUMBER)
					{
						prop = child.GetIntProp(Node.ISNUMBER_PROP, -1);
					}
					if (prop != -1)
					{
						child.RemoveProp(Node.ISNUMBER_PROP);
						GenerateExpression(il, child, node);
						child.PutIntProp(Node.ISNUMBER_PROP, prop);
					}
					else
					{
						GenerateExpression(il, child, node);
					}
					break;
				}

				case Token.IN:
				case Token.INSTANCEOF:
				case Token.LE:
				case Token.LT:
				case Token.GE:
				case Token.GT:
				{
					var trueGOTO = il.DefineLabel();
					var falseGOTO = il.DefineLabel();
					VisitIfJumpRelOp(il, node, child, trueGOTO, falseGOTO);
					AddJumpedBooleanWrap(il, trueGOTO, falseGOTO);
					break;
				}

				case Token.EQ:
				case Token.NE:
				case Token.SHEQ:
				case Token.SHNE:
				{
					var trueGOTO = il.DefineLabel();
					var falseGOTO = il.DefineLabel();
					VisitIfJumpEqOp(il, node, child, trueGOTO, falseGOTO);
					AddJumpedBooleanWrap(il, trueGOTO, falseGOTO);
					break;
				}

				case Token.GETPROP:
				case Token.GETPROPNOWARN:
				{
					VisitGetProp(il, node, child);
					break;
				}

				case Token.GETELEM:
				{
					GenerateExpression(il, child, node);
					// object
					GenerateExpression(il, child.GetNext(), node);
					// id
					il.Emit(OpCodes.Ldarg_1);
					if (node.GetIntProp(Node.ISNUMBER_PROP, -1) != -1)
					{
						AddScriptRuntimeInvoke(il, "GetObjectIndex", new[] { typeof (object), typeof (double), typeof (Context) });
					}
					else
					{
						il.Emit(OpCodes.Ldarg_2);
						AddScriptRuntimeInvoke(il, "GetObjectElem", new[] { typeof (Object), typeof (Object), typeof (Context), typeof (Scriptable) });
					}
					break;
				}

				case Token.GET_REF:
				{
					GenerateExpression(il, child, node);
					// reference
					il.Emit(OpCodes.Ldarg_1);
					AddScriptRuntimeInvoke(il, "RefGet", new[] { typeof (Ref), typeof (Context) });
					break;
				}

				case Token.GETVAR:
				{
					VisitGetVar(il, node);
					break;
				}

				case Token.SETVAR:
				{
					VisitSetVar(il, node, child, true);
					break;
				}

				case Token.SETNAME:
				{
					VisitSetName(il, node, child);
					break;
				}

				case Token.STRICT_SETNAME:
				{
					VisitStrictSetName(il, node, child);
					break;
				}

				case Token.SETCONST:
				{
					VisitSetConst(il, node, child);
					break;
				}

				case Token.SETCONSTVAR:
				{
					VisitSetConstVar(il, node, child, true);
					break;
				}

				case Token.SETPROP:
				case Token.SETPROP_OP:
				{
					VisitSetProp(il, type, node, child);
					break;
				}

				case Token.SETELEM:
				case Token.SETELEM_OP:
				{
					VisitSetElem(il, type, node, child);
					break;
				}

				case Token.SET_REF:
				case Token.SET_REF_OP:
				{
					GenerateExpression(il, child, node);
					child = child.GetNext();
					if (type == Token.SET_REF_OP)
					{
						il.Emit(OpCodes.Dup);
						il.Emit(OpCodes.Ldarg_1);
						AddScriptRuntimeInvoke(il, "RefGet", new[] { typeof (Ref), typeof (Context) });
					}
					GenerateExpression(il, child, node);
					il.Emit(OpCodes.Ldarg_1);
					AddScriptRuntimeInvoke(il, "RefSet", new[] { typeof (Ref), typeof (Object), typeof (Context) });
					break;
				}

				case Token.DEL_REF:
				{
					GenerateExpression(il, child, node);
					il.Emit(OpCodes.Ldarg_1);
					AddScriptRuntimeInvoke(il, "RefDel", new[] { typeof (Ref), typeof (Context) });
					break;
				}

				case Token.DELPROP:
				{
					var isName = child.GetType() == Token.BINDNAME;
					GenerateExpression(il, child, node);
					child = child.GetNext();
					GenerateExpression(il, child, node);
					il.Emit(OpCodes.Ldarg_1);
					il.EmitLoadConstant(isName);
					AddScriptRuntimeInvoke(il, "Delete", new[] { typeof(Object), typeof(Object), typeof(Context), typeof(bool) });
					break;
				}

				case Token.BINDNAME:
				{
					while (child != null)
					{
						GenerateExpression(il, child, node);
						child = child.GetNext();
					}
					// Generate code for "ScriptRuntime.bind(varObj, "s")"
					il.Emit(OpCodes.Ldarg_1);
					il.Emit(OpCodes.Ldarg_2);
					il.EmitLoadConstant(node.GetString());
					AddScriptRuntimeInvoke(il, "Bind", typeof (Context), typeof (Scriptable), typeof (String));
					break;
				}

				case Token.LOCAL_LOAD:
				{
					var local = GetLocalBlockRegister(node);
					il.EmitLoadLocal(local);
					break;
				}

				case Token.REF_SPECIAL:
				{
					var special = (string)node.GetProp(Node.NAME_PROP);
					GenerateExpression(il, child, node);
					il.EmitLoadConstant(special);
					il.Emit(OpCodes.Ldarg_1);
					AddScriptRuntimeInvoke(il, "SpecialRef", typeof (Object), typeof (String), typeof (Context));
					break;
				}

				case Token.REF_MEMBER:
				case Token.REF_NS_MEMBER:
				case Token.REF_NAME:
				case Token.REF_NS_NAME:
				{
					var memberTypeFlags = node.GetIntProp(Node.MEMBER_TYPE_PROP, 0);
					do
					{
						// generate possible target, possible namespace and member
						GenerateExpression(il, child, node);
						child = child.GetNext();
					}
					while (child != null);
					il.Emit(OpCodes.Ldarg_1);
					string methodName;
					Type[] types;
					switch (type)
					{
						case Token.REF_MEMBER:
						{
							methodName = "MemberRef";
							types = new[] { typeof (Object), typeof (Object), typeof (Context), typeof (int) };
							break;
						}

						case Token.REF_NS_MEMBER:
						{
							methodName = "MemberRef";
							types = new[] { typeof (Object), typeof (Object), typeof (Object), typeof (Context), typeof (int) };
							break;
						}

						case Token.REF_NAME:
						{
							methodName = "NameRef";
							types = new[] { typeof (Object), typeof (Context), typeof (Scriptable), typeof (int) };
							il.Emit(OpCodes.Ldarg_2);
							break;
						}

						case Token.REF_NS_NAME:
						{
							methodName = "NameRef";
							types = new[] { typeof (Object), typeof (Object), typeof (Context), typeof (Scriptable), typeof (int) };
							il.Emit(OpCodes.Ldarg_2);
							break;
						}

						default:
						{
							throw Kit.CodeBug();
						}
					}
					il.EmitLoadConstant(memberTypeFlags);
					AddScriptRuntimeInvoke(il, methodName, types);
					break;
				}

				case Token.DOTQUERY:
				{
					VisitDotQuery(il, node, child);
					break;
				}

				case Token.ESCXMLATTR:
				{
					GenerateExpression(il, child, node);
					il.Emit(OpCodes.Ldarg_1);
					AddScriptRuntimeInvoke(il, "EscapeAttributeValue", typeof (Object), typeof (Context));
					break;
				}

				case Token.ESCXMLTEXT:
				{
					GenerateExpression(il, child, node);
					il.Emit(OpCodes.Ldarg_1);
					AddScriptRuntimeInvoke(il, "EscapeTextValue", typeof (Object), typeof (Context));
					break;
				}

				case Token.DEFAULTNAMESPACE:
				{
					GenerateExpression(il, child, node);
					il.Emit(OpCodes.Ldarg_1);
					AddScriptRuntimeInvoke(il, "SetDefaultNamespace", typeof (Object), typeof (Context));
					break;
				}

				case Token.YIELD:
				{
					GenerateYieldPoint(il, node, true);
					break;
				}

				case Token.WITHEXPR:
				{
					var enterWith = child;
					var with = enterWith.GetNext();
					var leaveWith = with.GetNext();
					GenerateStatement(il, enterWith);
					GenerateExpression(il, with.GetFirstChild(), with);
					GenerateStatement(il, leaveWith);
					break;
				}

				case Token.ARRAYCOMP:
				{
					var initStmt = child;
					var expr = child.GetNext();
					GenerateStatement(il, initStmt);
					GenerateExpression(il, expr, node);
					break;
				}

				default:
				{
					throw new Exception("Unexpected node type " + type);
				}
			}
		}

		private void GenerateYieldPoint(ILGenerator il, Node node, bool exprContext)
		{
			// save stack state
			int top = /*cfw.GetStackTop()*/0;
			maxStack = maxStack > top ? maxStack : top;
			if (/*cfw.GetStackTop()*/0 != 0)
			{
				GenerateGetGeneratorStackState(il);
				for (var i = 0; i < top; i++)
				{
					il.EmitDupX1();
					il.Emit(ByteCode.SWAP);
					il.EmitLoadConstant(i);
					il.Emit(ByteCode.SWAP);
					il.Emit(OpCodes.Stelem_Ref);
				}
				// pop the array object
				il.Emit(OpCodes.Pop);
			}
			// generate the yield argument
			var child = node.GetFirstChild();
			if (child != null)
			{
				GenerateExpression(il, child, node);
			}
			else
			{
				Codegen.PushUndefined(il);
			}
			// change the resumption state
			var nextState = GetNextGeneratorState(node);
			GenerateSetGeneratorResumptionPoint(il, nextState);
			var hasLocals = GenerateSaveLocals(il, node);
			il.Emit(OpCodes.Ret);
			GenerateCheckForThrowOrClose(il, GetTargetLabel(il, node), hasLocals, nextState);
			// reconstruct the stack
			if (top != 0)
			{
				GenerateGetGeneratorStackState(il);
				for (var i = 0; i < top; i++)
				{
					il.Emit(OpCodes.Dup);
					il.EmitLoadConstant(top - i - 1);
					il.Emit(OpCodes.Ldelem_Ref);
					il.Emit(ByteCode.SWAP);
				}
				il.Emit(OpCodes.Pop);
			}
			// load return value from yield
			if (exprContext)
			{
				il.EmitLoadArgument(argsArgument);
			}
		}

		private void GenerateCheckForThrowOrClose(ILGenerator il, Label? label, bool hasLocals, int nextState)
		{
			var throwLabel = il.DefineLabel();
			var closeLabel = il.DefineLabel();
			// throw the user provided object, if the operation is .throw()
			il.MarkLabel(throwLabel);
			il.EmitLoadArgument(argsArgument);
			GenerateThrowJavaScriptException(il);
			// throw our special internal exception if the generator is being closed
			il.MarkLabel(closeLabel);
			il.EmitLoadArgument(argsArgument);
			il.Emit(OpCodes.Castclass, typeof(Exception));
			il.Emit(OpCodes.Throw);
			// mark the re-entry point
			// jump here after initializing the locals
			if (label != null)
			{
				il.MarkLabel(label.Value);
			}
			if (!hasLocals)
			{
				// jump here directly if there are no locals
				il.MarkLabel(generatorSwitch[nextState]);
			}
			// see if we need to dispatch for .close() or .throw()
			il.EmitLoadArgument(operationLocal);
			il.EmitLoadConstant(NativeGenerator.GENERATOR_CLOSE);
			il.Emit(OpCodes.Ceq);
			il.Emit(OpCodes.Brtrue, closeLabel);
			il.EmitLoadArgument(operationLocal);
			il.EmitLoadConstant(NativeGenerator.GENERATOR_THROW);
			il.Emit(OpCodes.Ceq);
			il.Emit(OpCodes.Brtrue, throwLabel);
		}

		private void GenerateIfJump(ILGenerator il, Node node, Node parent, Label trueLabel, Label falseLabel)
		{
			// System.out.println("gen code for " + node.toString());
			var type = node.GetType();
			var child = node.GetFirstChild();
			switch (type)
			{
				case Token.NOT:
				{
					GenerateIfJump(il, child, node, falseLabel, trueLabel);
					break;
				}

				case Token.OR:
				case Token.AND:
				{
					var interLabel = il.DefineLabel();
					if (type == Token.AND)
					{
						GenerateIfJump(il, child, node, interLabel, falseLabel);
					}
					else
					{
						GenerateIfJump(il, child, node, trueLabel, interLabel);
					}
					il.MarkLabel(interLabel);
					child = child.GetNext();
					GenerateIfJump(il, child, node, trueLabel, falseLabel);
					break;
				}

				case Token.IN:
				case Token.INSTANCEOF:
				case Token.LE:
				case Token.LT:
				case Token.GE:
				case Token.GT:
				{
					VisitIfJumpRelOp(il, node, child, trueLabel, falseLabel);
					break;
				}

				case Token.EQ:
				case Token.NE:
				case Token.SHEQ:
				case Token.SHNE:
				{
					VisitIfJumpEqOp(il, node, child, trueLabel, falseLabel);
					break;
				}

				default:
				{
					// Generate generic code for non-optimized jump
					GenerateExpression(il, node, parent);
					AddScriptRuntimeInvoke(il, "ToBoolean", typeof (Object));
					il.Emit(OpCodes.Brtrue, trueLabel);
					il.Emit(OpCodes.Br, falseLabel);
					break;
				}
			}
		}

		private void VisitFunction(ILGenerator il, OptFunctionNode ofn, int functionType)
		{
			var fnIndex = codegen.GetIndex(ofn.fnode);
			// Call function constructor
			il.Emit(OpCodes.Ldarg_2); // Scriptable
			il.Emit(OpCodes.Ldarg_1); // load 'cx'
			il.EmitLoadConstant(fnIndex);
			il.Emit(OpCodes.Newobj, constructor);
			if (functionType == FunctionNode.FUNCTION_EXPRESSION)
			{
				// Leave closure object on stack and do not pass it to
				// initFunction which suppose to connect statements to scope
				return;
			}
			il.EmitLoadConstant(functionType); // functionType
			il.Emit(OpCodes.Ldarg_2); // Scriptable
			il.Emit(OpCodes.Ldarg_1); // load 'cx'

			AddOptRuntimeInvoke(il, "InitFunction", new[] {typeof (NativeFunction), typeof (int), typeof (Scriptable), typeof (Context)});
		}

		private readonly IList<Label> labels = new List<Label>();

		private Label GetTargetLabel(ILGenerator il, Node target)
		{
			int labelId = target.LabelId();
			if (labelId == -1)
			{
				labelId = labels.Count;
				labels.Add(il.DefineLabel());
				target.LabelId(labelId);
			}
			return labels[labelId];
		}

		private void VisitGoto(ILGenerator il, Jump node, int type, Node child)
		{
			var target = node.target;
			if (type == Token.IFEQ || type == Token.IFNE)
			{
				if (child == null)
				{
					throw Codegen.BadTree();
				}
				var targetLabel = GetTargetLabel(il, target);
				var fallThruLabel = il.DefineLabel();
				if (type == Token.IFEQ)
				{
					GenerateIfJump(il, child, node, targetLabel, fallThruLabel);
				}
				else
				{
					GenerateIfJump(il, child, node, fallThruLabel, targetLabel);
				}
				il.MarkLabel(fallThruLabel);
			}
			else
			{
				if (type == Token.JSR)
				{
					if (isGenerator)
					{
						//AddGotoWithReturn(il, target);
					    //InlineFinally(il, target);
					}
					else
					{
						// This assumes that JSR is only ever used for finally
						il.BeginFinallyBlock();
						InlineFinally(il, target);
					}
				}
				else
				{
					AddGoto(il, OpCodes.Br, target);
				}
			}
		}

		private void AddGotoWithReturn(ILGenerator il, Node target)
		{
			var ret = finallys.GetValueOrDefault(target);
			il.EmitLoadConstant(ret.jsrPoints.Count);
			AddGoto(il, OpCodes.Br, target);
			var retLabel = il.DefineLabel();
			il.MarkLabel(retLabel);
			//ret.jsrPoints.Add(retLabel);
		}

		private MethodBuilder GenerateArrayLiteralFactory(Node node, int count)
		{
			var methodName = codegen.GetBodyMethodName(scriptOrFn) + "_literal" + count;
			argsArgument = firstFreeLocal++;
			localsMax = firstFreeLocal;
			var method = tb.DefineMethod(methodName, MethodAttributes.Private, typeof (Scriptable), new[] { typeof (Context), typeof (Scriptable), typeof (Scriptable), typeof (Object[]) });
			var il = method.GetILGenerator();
			VisitArrayLiteral(il, node, node.GetFirstChild(), true);
			il.Emit(OpCodes.Ret);
			return method;
		}

		private MethodBuilder GenerateObjectLiteralFactory(Node node, int count)
		{
			var methodName = codegen.GetBodyMethodName(scriptOrFn) + "_literal" + count;
			argsArgument = firstFreeLocal++;
			localsMax = firstFreeLocal;
			var method = tb.DefineMethod(methodName, MethodAttributes.Private, typeof (Scriptable), new[] { typeof (Context), typeof (Scriptable), typeof (Scriptable), typeof (Object[]) });
			var il = method.GetILGenerator();
			VisitObjectLiteral(il, node, node.GetFirstChild(), true);
			il.Emit(OpCodes.Ret);
			return method;
		}

		private void VisitArrayLiteral(ILGenerator il, Node node, Node child, bool topLevel)
		{
			var count = 0;
			for (var cursor = child; cursor != null; cursor = cursor.GetNext())
			{
				++count;
			}
			// If code budget is tight swap out literals into separate method
			if (!topLevel && (count > 10 || il.ILOffset > 30000) && !hasVarsInRegs && !isGenerator && !inLocalBlock)
			{
				var method = Clone().GenerateArrayLiteralFactory(node, identityGenerator.GetNextId());
				il.Emit(OpCodes.Ldarg_0);
				il.Emit(OpCodes.Ldarg_1);
				il.Emit(OpCodes.Ldarg_2);
				il.Emit(OpCodes.Ldarg_3);
				il.EmitLoadArgument(argsArgument);
				il.Emit(OpCodes.Callvirt, method);
				return;
			}
			// load array to store array literal objects
			AddNewObjectArray(il, count);
			for (var i = 0; i != count; ++i)
			{
				il.Emit(OpCodes.Dup);
				il.EmitLoadConstant(i);
				GenerateExpression(il, child, node);
				il.Emit(OpCodes.Stelem_Ref);
				child = child.GetNext();
			}
			var skipIndexes = (int[])node.GetProp(Node.SKIP_INDEXES_PROP);
			if (skipIndexes == null)
			{
				il.Emit(OpCodes.Ldnull);
				il.Emit(OpCodes.Ldc_I4_0);
			}
			else
			{
				string k1 = OptRuntime.EncodeIntArray(skipIndexes);
				il.EmitLoadConstant(k1);
				var k = skipIndexes.Length;
				il.EmitLoadConstant(k);
			}
			il.Emit(OpCodes.Ldarg_1);
			il.Emit(OpCodes.Ldarg_2);
			AddOptRuntimeInvoke(il, "NewArrayLiteral", new[] {typeof (object[]), typeof (string), typeof (int), typeof (Context), typeof (Scriptable)});
		}

		private void VisitObjectLiteral(ILGenerator il, Node node, Node child, bool topLevel)
		{
			var properties = (object[]) node.GetProp(Node.OBJECT_IDS_PROP);
			var count = properties.Length;
			// If code budget is tight swap out literals into separate method
			if (!topLevel && (count > 10 || il.ILOffset > 30000) && !hasVarsInRegs && !isGenerator && !inLocalBlock)
			{
				var method = Clone().GenerateObjectLiteralFactory(node, identityGenerator.GetNextId());
				il.Emit(OpCodes.Ldarg_0);
				il.Emit(OpCodes.Ldarg_1);
				il.Emit(OpCodes.Ldarg_2);
				il.Emit(OpCodes.Ldarg_3);
				il.EmitLoadArgument(argsArgument);
				il.Emit(OpCodes.Callvirt, method);
				return;
			}
			// load array with property ids
			AddNewObjectArray(il, count);
			for (var i = 0; i < count; i++)
			{
				il.Emit(OpCodes.Dup);
				il.EmitLoadConstant(i);
				var id = properties[i];
				var s = id as string;
				if (s != null)
				{
					il.EmitLoadConstant(s);
				}
				else
				{
					il.EmitLoadConstant((int) id);
					il.Emit(OpCodes.Box, typeof (int));
				}
				il.Emit(OpCodes.Stelem_Ref);
			}
			// load array with property values
			AddNewObjectArray(il, count);
			var child2 = child;
			for (var i = 0; i < count; i++)
			{
				il.Emit(OpCodes.Dup);
				il.EmitLoadConstant(i);
				var childType = child2.GetType();
				if (childType == Token.GET || childType == Token.SET)
				{
					GenerateExpression(il, child2.GetFirstChild(), node);
				}
				else
				{
					GenerateExpression(il, child2, node);
				}
				il.Emit(OpCodes.Stelem_Ref);
				child2 = child2.GetNext();
			}
			// check if object literal actually has any getters or setters
			var hasGetterSetters = false;
			child2 = child;
			for (var i = 0; i < count; i++)
			{
				var childType = child2.GetType();
				if (childType == Token.GET || childType == Token.SET)
				{
					hasGetterSetters = true;
					break;
				}
				child2 = child2.GetNext();
			}
			// create getter/setter flag array
			if (hasGetterSetters)
			{
				il.EmitLoadConstant(count);
				il.Emit(OpCodes.Newarr, typeof(int));
				child2 = child;
				for (var i = 0; i < count; i++)
				{
					il.Emit(OpCodes.Dup);
					il.EmitLoadConstant(i);
					var childType = child2.GetType();
					switch (childType)
					{
						case Token.GET:
							il.Emit(OpCodes.Ldc_I4_M1);
							break;
						case Token.SET:
							il.Emit(OpCodes.Ldc_I4_1);
							break;
						default:
							il.Emit(OpCodes.Ldc_I4_0);
							break;
					}
					il.Emit(OpCodes.Stelem_I4);
					child2 = child2.GetNext();
				}
			}
			else
			{
				il.Emit(OpCodes.Ldnull);
			}
			il.Emit(OpCodes.Ldarg_1);
			il.Emit(OpCodes.Ldarg_2);
			AddScriptRuntimeInvoke(il, "NewObjectLiteral", new[] { typeof (Object[]), typeof (Object[]), typeof (int[]), typeof (Context), typeof (Scriptable) });
		}

		public BodyCodegen Clone()
		{
			var bodygen = new BodyCodegen
			{
				tb = tb,
				codegen = codegen,
				compilerEnv = compilerEnv,
				scriptOrFn = scriptOrFn,
				scriptOrFnIndex = scriptOrFnIndex,
				isGenerator = isGenerator,
				constructor = constructor,
				regExpInit = regExpInit,
				identityGenerator = identityGenerator,
                maxLocals = maxLocals
			};
			bodygen.InitBodyGeneration();
			return bodygen;
		}

		private void VisitSpecialCall(ILGenerator il, Node node, int type, int specialType, Node child)
		{
			il.Emit(OpCodes.Ldarg_1);
			if (type == Token.NEW)
			{
				GenerateExpression(il, child, node);
			}
			else
			{
				// stack: ... cx functionObj
				GenerateFunctionAndThisObj(il, child, node);
			}
			// stack: ... cx functionObj thisObj
			child = child.GetNext();
			GenerateCallArgArray(il, node, child, false);
			il.Emit(OpCodes.Ldarg_2);
			il.Emit(OpCodes.Ldarg_3);
			// call type
			il.EmitLoadConstant(specialType);
			if (type == Token.NEW)
			{
				AddOptRuntimeInvoke(il, "NewObjectSpecial", typeof (Context), typeof (Object), typeof (Object[]), typeof (Scriptable), typeof (Scriptable), typeof (int));
			}
			else
			{
				// filename, linenumber
				il.EmitLoadConstant(scriptOrFn.GetSourceName() ?? String.Empty);
				il.EmitLoadConstant(itsLineNumber);
				AddOptRuntimeInvoke(il, "CallSpecial", typeof (Context), typeof (Callable), typeof (Scriptable), typeof (Object[]), typeof (Scriptable), typeof (Scriptable), typeof (int), typeof (string), typeof (int));
			}
		}

		private void VisitStandardCall(ILGenerator il, Node node, Node child)
		{
			if (node.GetType() != Token.CALL)
			{
				throw Codegen.BadTree();
			}
			var firstArgChild = child.GetNext();
			var childType = child.GetType();
			string methodName;
			Type[] signature;
			if (firstArgChild == null)
			{
				if (childType == Token.NAME)
				{
					// name() call
					var name = child.GetString();
					il.EmitLoadConstant(name);
					methodName = "CallName0";
					signature = new[] { typeof (String), typeof (Context), typeof (Scriptable) };
				}
				else
				{
					if (childType == Token.GETPROP)
					{
						// x.name() call
						var propTarget = child.GetFirstChild();
						GenerateExpression(il, propTarget, node);
						var id = propTarget.GetNext();
						var property = id.GetString();
						il.EmitLoadConstant(property);
						methodName = "CallProp0";
						signature = new[] { typeof (Object), typeof (String), typeof (Context), typeof (Scriptable) };
					}
					else
					{
						if (childType == Token.GETPROPNOWARN)
						{
							throw Kit.CodeBug();
						}
						else
						{
							GenerateFunctionAndThisObj(il, child, node);
							methodName = "Call0";
							signature = new[] { typeof (Callable), typeof (Scriptable), typeof (Context), typeof (Scriptable) };
						}
					}
				}
			}
			else
			{
				if (childType == Token.NAME)
				{
					// XXX: this optimization is only possible if name
					// resolution
					// is not affected by arguments evaluation and currently
					// there are no checks for it
					var name = child.GetString();
					GenerateCallArgArray(il, node, firstArgChild, false);
					il.EmitLoadConstant(name);
					methodName = "CallName";
					signature = new[] { typeof (object[]), typeof (String), typeof (Context), typeof (Scriptable) };
				}
				else
				{
					var argCount = 0;
					for (var arg = firstArgChild; arg != null; arg = arg.GetNext())
					{
						++argCount;
					}
					GenerateFunctionAndThisObj(il, child, node);
					// stack: ... functionObj thisObj
					if (argCount == 1)
					{
						GenerateExpression(il, firstArgChild, node);
						methodName = "Call1";
						signature = new[] { typeof (Callable), typeof (Scriptable), typeof (Object), typeof (Context), typeof (Scriptable) };
					}
					else
					{
						if (argCount == 2)
						{
							GenerateExpression(il, firstArgChild, node);
							GenerateExpression(il, firstArgChild.GetNext(), node);
							methodName = "Call2";
							signature = new[] { typeof (Callable), typeof (Scriptable), typeof (Object), typeof (Object), typeof (Context), typeof (Scriptable) };
						}
						else
						{
							GenerateCallArgArray(il, node, firstArgChild, false);
							methodName = "CallN";
							signature = new[] { typeof (Callable), typeof (Scriptable), typeof (Object[]), typeof (Context), typeof (Scriptable) };
						}
					}
				}
			}
			il.Emit(OpCodes.Ldarg_1);
			il.Emit(OpCodes.Ldarg_2);
			AddOptRuntimeInvoke(il, methodName, signature);
		}

		private void VisitStandardNew(ILGenerator il, Node node, Node child)
		{
			if (node.GetType() != Token.NEW)
			{
				throw Codegen.BadTree();
			}
			var firstArgChild = child.GetNext();
			GenerateExpression(il, child, node);
			// stack: ... functionObj
			il.Emit(OpCodes.Ldarg_1);
			il.Emit(OpCodes.Ldarg_2);
			// stack: ... functionObj cx scope
			GenerateCallArgArray(il, node, firstArgChild, false);
			AddScriptRuntimeInvoke(il, "NewObject", typeof (Object), typeof (Context), typeof (Scriptable), typeof (Object[]));
		}

		private void VisitOptimizedCall(ILGenerator il, Node node, OptFunctionNode target, int type, Node child)
		{
			var firstArgChild = child.GetNext();

		    LocalBuilder thisObjLocal = null;
		    if (type == Token.NEW)
		    {
		        GenerateExpression(il, child, node);
		    }
		    else
		    {
		        GenerateFunctionAndThisObj(il, child, node);
		        thisObjLocal = DeclareLocal(il);
		        il.EmitStoreLocal(thisObjLocal);
		    }
		    // stack: ... functionObj
			var beyond = il.DefineLabel();
			var regularCall = il.DefineLabel();
			il.Emit(OpCodes.Dup);
			il.Emit(OpCodes.Isinst, tb);
			il.Emit(OpCodes.Brfalse, regularCall);
			il.Emit(OpCodes.Castclass, tb);
			il.Emit(OpCodes.Dup);
			il.Emit(OpCodes.Ldfld, tb.GetField(Codegen.ID_FIELD_NAME));
			var k = codegen.GetIndex(target.fnode);
			il.EmitLoadConstant(k);
			il.Emit(OpCodes.Ceq);
			il.Emit(OpCodes.Brfalse, regularCall);
			// stack: ... directFunct
			il.Emit(OpCodes.Ldarg_1);
			il.Emit(OpCodes.Ldarg_2);
			// stack: ... directFunc cx scope
			if (type == Token.NEW)
			{
				il.Emit(OpCodes.Ldnull);
			}
			else
			{
			    il.EmitLoadLocal(thisObjLocal);
			}
			// stack: ... directFunc cx scope thisObj
			var argChild = firstArgChild;
			while (argChild != null)
			{
				var dcp_register = NodeIsDirectCallParameter(argChild);
			    if (dcp_register != null)
			    {
			        dcp_register.EmitLoadSlot1(il);
			        dcp_register.EmitLoadSlot2(il);
			    }
			    else if (argChild.GetIntProp(Node.ISNUMBER_PROP, -1) == Node.BOTH)
			    {
			        il.Emit(OpCodes.Ldtoken, typeof (void));
			        GenerateExpression(il, argChild, node);
			    }
			    else
			    {
			        GenerateExpression(il, argChild, node);
			        il.EmitLoadConstant(0.0);
			    }
			    argChild = argChild.GetNext();
			}
			il.Emit(OpCodes.Ldsfld, typeof (ScriptRuntime).GetField("emptyArgs"));
			il.Emit(OpCodes.Call, tb.GetMethod((type == Token.NEW) ? codegen.GetDirectCtorName(target.fnode) : codegen.GetBodyMethodName(target.fnode), codegen.GetParameterTypes(target.fnode)));
			il.Emit(OpCodes.Br, beyond);
			il.MarkLabel(regularCall);
			// stack: ... functionObj
			il.Emit(OpCodes.Ldarg_1);
			il.Emit(OpCodes.Ldarg_2);
			// stack: ... functionObj cx scope
			if (type != Token.NEW)
			{
			    il.EmitLoadLocal(thisObjLocal);
			    ReleaseLocal(thisObjLocal);
                // stack: ... functionObj cx scope thisObj
			}
			
			// XXX: this will generate code for the child array the second time,
			// so expression code generation better not to alter tree structure...
			GenerateCallArgArray(il, node, firstArgChild, true);
			if (type == Token.NEW)
			{
				AddScriptRuntimeInvoke(il, "NewObject", typeof (Object), typeof (Context), typeof (Scriptable), typeof (Object[]));
			}
			else
			{
				il.Emit(OpCodes.Callvirt, typeof (Callable).GetMethod("Call", new[] { typeof (Context), typeof (Scriptable), typeof (Scriptable), typeof (Object[]) }));
			}
			il.MarkLabel(beyond);
		}

		private void GenerateCallArgArray(ILGenerator il, Node node, Node argChild, bool directCall)
		{
			var argCount = 0;
			for (var child = argChild; child != null; child = child.GetNext())
			{
				++argCount;
			}
			// load array object to set arguments
			if (argCount == 1 && itsOneArgArray != null)
			{
				il.EmitLoadLocal(itsOneArgArray);
			}
			else
			{
				AddNewObjectArray(il, argCount);
			}
			// Copy arguments into it
			for (var i = 0; i != argCount; ++i)
			{
				// If we are compiling a generator an argument could be the result
				// of a yield. In that case we will have an immediate on the stack
				// which we need to avoid
				if (!isGenerator)
				{
					il.Emit(OpCodes.Dup);
					il.EmitLoadConstant(i);
				}
				if (!directCall)
				{
					GenerateExpression(il, argChild, node);
				}
				else
				{
					// If this has also been a directCall sequence, the Number
					// flag will have remained set for any parameter so that
					// the values could be copied directly into the outgoing
					// args. Here we want to force it to be treated as not in
					// a Number context, so we set the flag off.
					var dcp_register = NodeIsDirectCallParameter(argChild);
					if (dcp_register != null)
					{
						DcpLoadAsObject(il, dcp_register);
					}
					else
					{
						GenerateExpression(il, argChild, node);
						var childNumberFlag = argChild.GetIntProp(Node.ISNUMBER_PROP, -1);
						if (childNumberFlag == Node.BOTH)
						{
						}
					}
				}
				// When compiling generators, any argument to a method may be a
				// yield expression. Hence we compile the argument first and then
				// load the argument index and assign the value to the args array.
				if (isGenerator)
				{
				    var tempLocal = DeclareLocal(il);
					il.EmitStoreLocal(tempLocal);
					il.Emit(OpCodes.Castclass, typeof(Object[]));
					il.Emit(OpCodes.Dup);
					il.EmitLoadConstant(i);
					il.EmitLoadLocal(tempLocal);
				    ReleaseLocal(tempLocal);
				}
				il.Emit(OpCodes.Stelem_Ref);
				argChild = argChild.GetNext();
			}
		}

		private void GenerateFunctionAndThisObj(ILGenerator il, Node node, Node parent)
		{
			// Place on stack (function object, function this) pair
			var type = node.GetType();
			switch (node.GetType())
			{
				case Token.GETPROPNOWARN:
				{
					throw Kit.CodeBug();
				}

				case Token.GETPROP:
				case Token.GETELEM:
				{
					var target = node.GetFirstChild();
					GenerateExpression(il, target, node);
					var id = target.GetNext();
					if (type == Token.GETPROP)
					{
						var property = id.GetString();
						il.EmitLoadConstant(property);
						il.Emit(OpCodes.Ldarg_1);
						il.Emit(OpCodes.Ldarg_2);
						AddScriptRuntimeInvoke(il, "GetPropFunctionAndThis", typeof (Object), typeof (String), typeof (Context), typeof (Scriptable));
					}
					else
					{
						GenerateExpression(il, id, node);
						// id
						if (node.GetIntProp(Node.ISNUMBER_PROP, -1) != -1)
						{
						}
						il.Emit(OpCodes.Ldarg_1);
						AddScriptRuntimeInvoke(il, "GetElemFunctionAndThis", typeof (Object), typeof (Object), typeof (Context));
					}
					break;
				}

				case Token.NAME:
				{
					var name = node.GetString();
					il.EmitLoadConstant(name);
					il.Emit(OpCodes.Ldarg_1);
					il.Emit(OpCodes.Ldarg_2);
					AddScriptRuntimeInvoke(il, "GetNameFunctionAndThis", typeof (String), typeof (Context), typeof (Scriptable));
					break;
				}

				default:
				{
					// including GETVAR
					GenerateExpression(il, node, parent);
					il.Emit(OpCodes.Ldarg_1);
					AddScriptRuntimeInvoke(il, "GetValueFunctionAndThis", typeof (Object), typeof (Context));
					break;
				}
			}
			// Get thisObj prepared by get(Name|Prop|Elem|Value)FunctionAndThis
			il.Emit(OpCodes.Ldarg_1);
			AddScriptRuntimeInvoke(il, "LastStoredScriptable", typeof (Context));
		}

		private void UpdateLineNumber(Node node, ILGenerator il)
		{
			itsLineNumber = node.GetLineno();
			if (itsLineNumber == -1)
			{
				return;
			}
			AddLineNumberEntry(il, itsLineNumber);
		}

		private void VisitTryCatchFinally(ILGenerator il, Jump node, Node child)
		{
			// OPT we only need to do this if there are enclosed WITH
			// statements; could statically check and omit this if there aren't any.
			// XXX OPT Maybe instead do syntactic transforms to associate
			// each 'with' with a try/finally block that does the exitwith.
		    var savedVariableObject = DeclareLocal(il);
			il.Emit(OpCodes.Ldarg_2);
			il.Emit(OpCodes.Stloc, savedVariableObject);
            il.EmitWriteLine(savedVariableObject);

			il.BeginExceptionBlock();
			var startLabel = il.DefineLabel();
			//il.MarkLabel(startLabel);

			var catchTarget = node.target;
			var finallyTarget = node.GetFinally();
			var handlerLabels = new Label[EXCEPTION_MAX];
			exceptionManager.PushExceptionInfo(node);
			if (catchTarget != null)
			{
				handlerLabels[JAVASCRIPT_EXCEPTION] = il.DefineLabel();
				handlerLabels[EVALUATOR_EXCEPTION] = il.DefineLabel();
				handlerLabels[ECMAERROR_EXCEPTION] = il.DefineLabel();
				var cx = Context.GetCurrentContext();
				if (cx != null && cx.HasFeature(LanguageFeatures.EnhancedJavaAccess))
				{
					handlerLabels[THROWABLE_EXCEPTION] = il.DefineLabel();
				}
			}
			if (finallyTarget != null)
			{
				handlerLabels[FINALLY_EXCEPTION] = il.DefineLabel();
			}

			//exceptionManager.SetHandlers(handlerLabels, startLabel);
			// create a table for the equivalent of JSR returns
			if (isGenerator && finallyTarget != null)
			{
				var ret = new FinallyReturnPoint();
				if (finallys == null)
				{
					finallys = new Dictionary<Node, FinallyReturnPoint>();
				}
				// add the finally target to hashtable
				finallys[finallyTarget] = ret;
				// add the finally node as well to the hash table
				finallys[finallyTarget.GetNext()] = ret;
			}
			var exceptionLocal = GetLocalBlockRegister(node);
			while (child != null)
			{
				if (child == catchTarget)
				{
					var catchLabel = GetTargetLabel(il, catchTarget);
					exceptionManager.RemoveHandler(JAVASCRIPT_EXCEPTION, catchLabel);
					exceptionManager.RemoveHandler(EVALUATOR_EXCEPTION, catchLabel);
					exceptionManager.RemoveHandler(ECMAERROR_EXCEPTION, catchLabel);
					exceptionManager.RemoveHandler(THROWABLE_EXCEPTION, catchLabel);
					il.BeginCatchBlock(typeof (RhinoException));
					//il.MarkLabel(catchLabel);
					Label? handler = handlerLabels [ECMAERROR_EXCEPTION];
					//il.BeginCatchBlock(typeof (RhinoException));
//			if (handler == null)
//			{
//				handler = il.DefineLabel();
//			}
					//cfw.MarkHandler(handler.Value);
					// MS JVM gets cranky if the exception object is left on the stack
                    il.EmitStoreLocal(exceptionLocal);
                    // reset the variable object local
                    il.EmitWriteLine(savedVariableObject);
                    il.EmitLoadLocal(savedVariableObject);
					il.EmitStoreArgument(2);
					//ExceptionTypeToName(exceptionType);
					//il.Emit(OpCodes.Br, ((Label?) catchLabel).Value);
				}
				GenerateStatement(il, child);
				child = child.GetNext();
			}
			// control flow skips the handlers
			var realEnd = il.DefineLabel();
			//il.Emit(OpCodes.Leave, realEnd);
			// javascript handler; unwrap exception and GOTO to javascript
			// catch area.
/*
			if (catchTarget != null)
			{
				// get the label to goto
				var catchLabel = GetTargetLabel(il, catchTarget);
				// If the function is a generator, then handlerLabels will consist
				// of zero labels. generateCatchBlock will create its own label
				// in this case. The extra parameter for the label is added for
				// the case of non-generator functions that inline finally blocks.
				GenerateCatchBlock(il, JAVASCRIPT_EXCEPTION, savedVariableObject, catchLabel, exceptionLocal, handlerLabels[JAVASCRIPT_EXCEPTION]);
				GenerateCatchBlock(il, EVALUATOR_EXCEPTION, savedVariableObject, catchLabel, exceptionLocal, handlerLabels[EVALUATOR_EXCEPTION]);
				GenerateCatchBlock(il, ECMAERROR_EXCEPTION, savedVariableObject, catchLabel, exceptionLocal, handlerLabels[ECMAERROR_EXCEPTION]);
				var cx = Context.GetCurrentContext();
				if (cx != null && cx.HasFeature(LanguageFeatures.EnhancedJavaAccess))
				{
					GenerateCatchBlock(il, THROWABLE_EXCEPTION, savedVariableObject, catchLabel, exceptionLocal, handlerLabels[THROWABLE_EXCEPTION]);
				}
			}
*/
			// finally handler; catch all exceptions, store to a local; JSR to
			// the finally, then re-throw.
			if (finallyTarget != null)
			{
				var finallyHandler = il.DefineLabel();
				var finallyEnd = il.DefineLabel();
				
				//cfw.MarkHandler(finallyHandler);
//				il.MarkLabel(finallyHandler);
				if (isGenerator)
				{
					//il.BeginFaultBlock();
				}
				else
				{
					//il.BeginFinallyBlock(); 
					//il.MarkLabel(handlerLabels[FINALLY_EXCEPTION]);
				}
				//il.EmitStoreLocal(exceptionLocal);
				// reset the variable object local
//				il.EmitLoadLocal(savedVariableObject);
//				il.EmitStoreArgument(2);
				// get the label to JSR to
				var finallyLabel = GetTargetLabel(il, finallyTarget);
				if (isGenerator)
				{
//					InlineFinally(il, finallyTarget);
					//AddGotoWithReturn(il, finallyTarget);
				}
				else
				{
//					InlineFinally(il, finallyTarget);

//					InlineFinally(il, finallyTarget, handlerLabels[FINALLY_EXCEPTION], finallyEnd);
				}
				// rethrow
				/*il.EmitLoadLocal(exceptionLocal);
				if (isGenerator)
				{
					il.Emit(OpCodes.Castclass, typeof(Exception));
				}
				il.Emit(OpCodes.Throw);*/
				//il.MarkLabel(finallyEnd);
				// mark the handler
				if (isGenerator)
				{
                    //cfw.AddExceptionHandler(startLabel, finallyLabel, finallyHandler, null); // catch any
				}
			}
		    ReleaseLocal(savedVariableObject);
			if (!isGenerator)
			{
				//il.EndExceptionBlock();
				//exceptionManager.PopExceptionInfo();
			}
			//il.BeginFinallyBlock();
			il.EndExceptionBlock();
			//il.MarkLabel(realEnd);
		}

		private const int JAVASCRIPT_EXCEPTION = 0;

		private const int EVALUATOR_EXCEPTION = 1;

		private const int ECMAERROR_EXCEPTION = 2;

		private const int THROWABLE_EXCEPTION = 3;

		private const int FINALLY_EXCEPTION = 4;

		private const int EXCEPTION_MAX = 5;

		// Finally catch-alls are technically Throwable, but we want a distinction
		// for the exception manager and we want to use a null string instead of
		// an explicit Throwable string.
		private void GenerateCatchBlock(ILGenerator il, int exceptionType, LocalBuilder savedVariableObject, Label? catchLabel, int exceptionLocal, Label? handler)
		{
			il.BeginCatchBlock(ExceptionTypeToName(exceptionType));
//			if (handler == null)
//			{
//				handler = il.DefineLabel();
//			}
			//cfw.MarkHandler(handler.Value);
			// MS JVM gets cranky if the exception object is left on the stack
			il.EmitStoreLocal(exceptionLocal);
			// reset the variable object local
			il.EmitLoadLocal(savedVariableObject);
			il.EmitStoreArgument(2);
			//ExceptionTypeToName(exceptionType);
			il.Emit(OpCodes.Br, catchLabel.Value);
		}

		private static Type ExceptionTypeToName(int exceptionType)
		{
			switch (exceptionType)
			{
				case JAVASCRIPT_EXCEPTION:
					return typeof(JavaScriptException);
				case EVALUATOR_EXCEPTION:
					return typeof(EvaluatorException);
				case ECMAERROR_EXCEPTION:
					return typeof(EcmaError);
				case THROWABLE_EXCEPTION:
					return typeof(Exception);
				case FINALLY_EXCEPTION:
					return null;
				default:
					throw Kit.CodeBug();
			}
		}

		/// <summary>Manages placement of exception handlers for non-generator functions.</summary>
		/// <remarks>
		/// Manages placement of exception handlers for non-generator functions.
		/// For generator functions, there are mechanisms put into place to emulate
		/// jsr by using a goto with a return label. That is one mechanism for
		/// implementing finally blocks. The other, which is implemented by Sun,
		/// involves duplicating the finally block where jsr instructions would
		/// normally be. However, inlining finally blocks causes problems with
		/// translating exception handlers. Instead of having one big bytecode range
		/// for each exception, we now have to skip over the inlined finally blocks.
		/// This class is meant to help implement this.
		/// Every time a try block is encountered during translation, exception
		/// information should be pushed into the manager, which is treated as a
		/// stack. The addHandler() and setHandlers() methods may be used to register
		/// exceptionHandlers for the try block; removeHandler() is used to reverse
		/// the operation. At the end of the try/catch/finally, the exception state
		/// for it should be popped.
		/// The important function here is markInlineFinally. This finds which
		/// finally block on the exception state stack is being inlined and skips
		/// the proper exception handlers until the finally block is generated.
		/// </remarks>
		private class ExceptionManager
		{
			internal ExceptionManager(BodyCodegen enclosing)
			{
				_enclosing = enclosing;
				exceptionInfo = new List<ExceptionInfo>();
			}

			/// <summary>Push a new try block onto the exception information stack.</summary>
			/// <remarks>Push a new try block onto the exception information stack.</remarks>
			/// <param name="node">
			/// an exception handling node (node.getType() ==
			/// Token.TRY)
			/// </param>
			internal virtual void PushExceptionInfo(Jump node)
			{
				var fBlock = _enclosing.GetFinallyAtTarget(node.GetFinally());
				var ei = new ExceptionInfo(node, fBlock);
				exceptionInfo.Add(ei);
			}

		    /// <summary>Remove an exception handler for the top try block.</summary>
			/// <remarks>Remove an exception handler for the top try block.</remarks>
			/// <param name="exceptionType">
			///     one of the integer constants representing an
			///     exception type
			/// </param>
			/// <param name="endLabel">
			///     a label representing the end of the last bytecode
			///     that should be handled by the exception
			/// </param>
			/// <returns>
			/// the label of the exception handler associated with the
			/// exception type
			/// </returns>
			internal virtual void RemoveHandler(int exceptionType, Label endLabel)
			{
				var top = GetTop();
				if (top.handlerLabels[exceptionType] != null)
				{
					EndCatch(top, exceptionType, endLabel);
					top.handlerLabels[exceptionType] = null;
				}
			}

		    /// <summary>Mark the start of an inlined finally block.</summary>
			/// <remarks>
			/// Mark the start of an inlined finally block.
			/// When a finally block is inlined, any exception handlers that are
			/// lexically inside of its try block should not cover the range of the
			/// exception block. We scan from the innermost try block outward until
			/// we find the try block that matches the finally block. For any block
			/// whose exception handlers that aren't currently stopped by a finally
			/// block, we stop the handlers at the beginning of the finally block
			/// and set it as the finally block that has stopped the handlers. This
			/// prevents other inlined finally blocks from prematurely ending skip
			/// ranges and creating bad exception handler ranges.
			/// </remarks>
			/// <param name="finallyBlock">the finally block that is being inlined</param>
			/// <param name="finallyStart">the label of the beginning of the inlined code</param>
			internal virtual void MarkInlineFinallyStart(Node finallyBlock, Label finallyStart)
			{
				// Traverse the stack in LIFO order until the try block
				// corresponding to the finally block has been reached. We must
				// traverse backwards because the earlier exception handlers in
				// the exception handler table have priority when determining which
				// handler to use. Therefore, we start with the most nested try
				// block and move outward.
				var iter = exceptionInfo.ListIterator(exceptionInfo.Count);
				while (iter.HasPrevious())
				{
					var ei = iter.Previous();
					for (var i = 0; i < EXCEPTION_MAX; i++)
					{
						if (ei.handlerLabels[i] != null && ei.currentFinally == null)
						{
							EndCatch(ei, i, finallyStart);
							ei.exceptionStarts[i] = null;
							ei.currentFinally = finallyBlock;
						}
					}
					if (ei.finallyBlock == finallyBlock)
					{
						break;
					}
				}
			}

			/// <summary>Mark the end of an inlined finally block.</summary>
			/// <remarks>
			/// Mark the end of an inlined finally block.
			/// For any set of exception handlers that have been stopped by the
			/// inlined block, resume exception handling at the end of the finally
			/// block.
			/// </remarks>
			/// <param name="finallyBlock">the finally block that is being inlined</param>
			/// <param name="finallyEnd">the label of the end of the inlined code</param>
			internal virtual void MarkInlineFinallyEnd(Node finallyBlock, Label finallyEnd)
			{
				var iter = exceptionInfo.ListIterator(exceptionInfo.Count);
				while (iter.HasPrevious())
				{
					var ei = iter.Previous();
					for (var i = 0; i < EXCEPTION_MAX; i++)
					{
						if (ei.handlerLabels[i] != null && ei.currentFinally == finallyBlock)
						{
							ei.exceptionStarts[i] = finallyEnd;
							ei.currentFinally = null;
						}
					}
					if (ei.finallyBlock == finallyBlock)
					{
						break;
					}
				}
			}

			/// <summary>
			/// Mark off the end of a bytecode chunk that should be handled by an
			/// exceptionHandler.
			/// </summary>
			/// <remarks>
			/// Mark off the end of a bytecode chunk that should be handled by an
			/// exceptionHandler.
			/// The caller of this method must appropriately mark the start of the
			/// next bytecode chunk or remove the handler.
			/// </remarks>
			private void EndCatch(ExceptionInfo ei, int exceptionType, Label catchEnd)
			{
				Label? exceptionStart = ei.exceptionStarts[exceptionType];
				if (exceptionStart == null)
				{
					throw new InvalidOperationException("bad exception start");
				}
				var currentStart = exceptionStart.Value;
				var currentStartPC = _enclosing.cfw.GetLabelPC(currentStart);
				var catchEndPC = _enclosing.cfw.GetLabelPC(catchEnd);
				if (currentStartPC != catchEndPC)
				{
					_enclosing.cfw.AddExceptionHandler(exceptionStart.Value, catchEnd, ei.handlerLabels[exceptionType].Value, ExceptionTypeToName(exceptionType));
				}
			}

			private ExceptionInfo GetTop()
			{
				return exceptionInfo.GetLast();
			}

			private class ExceptionInfo
			{
				internal ExceptionInfo(Jump node, Node finallyBlock)
				{
					this.node = node;
					this.finallyBlock = finallyBlock;
					handlerLabels = new Label?[EXCEPTION_MAX];
					exceptionStarts = new Label?[EXCEPTION_MAX];
					currentFinally = null;
				}

				internal Jump node;

				internal Node finallyBlock;

				internal Label?[] handlerLabels;

				internal Label?[] exceptionStarts;

				internal Node currentFinally;

				// The current finally block that has temporarily ended the
				// exception handler ranges
			}

			private List<ExceptionInfo> exceptionInfo;

			private readonly BodyCodegen _enclosing;
			// A stack of try/catch block information ordered by lexical scoping
		}

		private ExceptionManager exceptionManager;

		/// <summary>Inline a FINALLY node into the method bytecode.</summary>
		/// <remarks>
		/// Inline a FINALLY node into the method bytecode.
		/// This method takes a label that points to the real start of the finally
		/// block as implemented in the bytecode. This is because in some cases,
		/// the finally block really starts before any of the code in the Node. For
		/// example, the catch-all-rethrow finally block has a few instructions
		/// prior to the finally block made by the user.
		/// In addition, an end label that should be unmarked is given as a method
		/// parameter. It is the responsibility of any callers of this method to
		/// mark the label.
		/// The start and end labels of the finally block are used to exclude the
		/// inlined block from the proper exception handler. For example, an inlined
		/// finally block should not be handled by a catch-all-rethrow.
		/// </remarks>
		/// <param name="il"></param>
		/// <param name="finallyTarget">
		///     a TARGET node directly preceding a FINALLY node or
		///     a FINALLY node itself
		/// </param>
		/// <param name="finallyStart">
		///     a pre-marked label that indicates the actual start
		///     of the finally block in the bytecode.
		/// </param>
		/// <param name="finallyEnd">
		///     an unmarked label that will indicate the actual end
		///     of the finally block in the bytecode.
		/// </param>
		private void InlineFinally(ILGenerator il, Node finallyTarget, Label finallyStart, Label finallyEnd)
		{
			var fBlock = GetFinallyAtTarget(finallyTarget);
			fBlock.ResetTargets();
			var child = fBlock.GetFirstChild();
			exceptionManager.MarkInlineFinallyStart(fBlock, finallyStart);
			while (child != null)
			{
				GenerateStatement(il, child);
				child = child.GetNext();
			}
			exceptionManager.MarkInlineFinallyEnd(fBlock, finallyEnd);
		}

		private void InlineFinally(ILGenerator il, Node finallyTarget)
		{
			var finallyStart = il.DefineLabel();
			var finallyEnd = il.DefineLabel();
			il.MarkLabel(finallyStart);
			InlineFinally(il, finallyTarget, finallyStart, finallyEnd);
			il.MarkLabel(finallyEnd);
		}

		/// <summary>Get a FINALLY node at a point in the IR.</summary>
		/// <remarks>
		/// Get a FINALLY node at a point in the IR.
		/// This is strongly dependent on the generated IR. If the node is a TARGET,
		/// it only check the next node to see if it is a FINALLY node.
		/// </remarks>
		private Node GetFinallyAtTarget(Node node)
		{
			if (node == null)
			{
				return null;
			}
			else
			{
				if (node.GetType() == Token.FINALLY)
				{
					return node;
				}
				else
				{
					if (node != null && node.GetType() == Token.TARGET)
					{
						var fBlock = node.GetNext();
						if (fBlock != null && fBlock.GetType() == Token.FINALLY)
						{
							return fBlock;
						}
					}
				}
			}
			throw Kit.CodeBug("bad finally target");
		}

		private bool GenerateSaveLocals(ILGenerator il, Node node)
		{
			if (locals2.Count == 0)
			{
				((FunctionNode)scriptOrFn).AddLiveLocals(node, null);
				return false;
			}
			// calculate the max locals
            maxLocals = maxLocals > locals2.Count ? maxLocals : locals2.Count;
			// create a locals list
            var ls = locals2.Select(x => x.LocalIndex).ToArray();
		    // save the locals
		    ((FunctionNode)scriptOrFn).AddLiveLocals(node, ls);
			// save locals
			GenerateGetGeneratorLocalsState(il);
            for (var i = 0; i < ls.Length; i++)
			{
				il.Emit(OpCodes.Dup);
			    il.EmitLoadConstant(i);
				il.EmitLoadLocal(ls[i]);
				il.Emit(OpCodes.Stelem_Ref);
			}
			// pop the array off the stack
			il.Emit(OpCodes.Pop);
			return true;
		}

		private void VisitSwitch(ILGenerator il, Node switchNode, Node child)
		{
			// See comments in IRFactory.createSwitch() for description
			// of SWITCH node
			GenerateExpression(il, child, switchNode);
			// save selector value
		    var selector = DeclareLocal(il);
			il.EmitStoreLocal(selector);
			for (var caseNode = (Jump)child.GetNext(); caseNode != null; caseNode = (Jump)caseNode.GetNext())
			{
				if (caseNode.GetType() != Token.CASE)
				{
					throw Codegen.BadTree();
				}
				var test = caseNode.GetFirstChild();
				GenerateExpression(il, test, caseNode);
				il.EmitLoadLocal(selector);
				AddScriptRuntimeInvoke(il, "ShallowEq", typeof (Object), typeof (Object));
				AddGoto(il, OpCodes.Brtrue, caseNode.target);
			}
		    ReleaseLocal(selector);
		}

		private void VisitTypeOfName(ILGenerator il, Node node)
		{
			if (hasVarsInRegs)
			{
				var varIndex = fnCurrent.fnode.GetIndexForNameNode(node);
				if (varIndex >= 0)
				{
					if (fnCurrent.IsNumberVar(varIndex))
					{
						il.EmitLoadConstant("number");
					}
					else
					{
						var dcp_register = varRegisters[varIndex];
						if (VarIsDirectCallParameter(varIndex))
						{
							dcp_register.EmitLoadSlot1(il);
							il.Emit(OpCodes.Ldtoken, typeof(void));
							var isNumberLabel = il.DefineLabel();
							il.Emit(OpCodes.Beq, isNumberLabel);
							dcp_register.EmitLoadSlot1(il);
							AddScriptRuntimeInvoke(il, "TypeOf", typeof(Object));
							var beyond = il.DefineLabel();
							il.Emit(OpCodes.Br, beyond);
							il.MarkLabel(isNumberLabel);
							//itsStackTop = stackTop;
							il.EmitLoadConstant("number");
							il.MarkLabel(beyond);
						}
						else
						{
							dcp_register.EmitLoadSlot1(il);
							AddScriptRuntimeInvoke(il, "TypeOf", typeof (Object));
						}
					}
					return;
				}
			}
			il.Emit(OpCodes.Ldarg_2);
			string k = node.GetString();
			il.EmitLoadConstant(k);
			AddScriptRuntimeInvoke(il, "TypeOfName", typeof (Scriptable), typeof (String));
		}

		/// <summary>Save the current code offset.</summary>
		/// <param name="il"></param>
		/// <remarks>
		/// Save the current code offset. This saved code offset is used to
		/// compute instruction counts in subsequent calls to
		/// <see cref="AddInstructionCount()">AddInstructionCount()</see>
		/// .
		/// </remarks>
		private void SaveCurrentCodeOffset(ILGenerator il)
		{
			savedCodeOffset = il.ILOffset;
		}

		/// <summary>
		/// Generate calls to ScriptRuntime.addInstructionCount to keep track of
		/// executed instructions and call <code>observeInstructionCount()</code>
		/// if a threshold is exceeded.<br />
		/// Calculates the count from getCurrentCodeOffset - savedCodeOffset
		/// </summary>
		/// <param name="il"></param>
		private void AddInstructionCount(ILGenerator il)
		{
			var count = il.ILOffset - savedCodeOffset;
			// TODO we used to return for count == 0 but that broke the following:
			//    while(true) continue; (see bug 531600)
			// To be safe, we now always count at least 1 instruction when invoked.
			AddInstructionCount(il, Math.Max(count, 1));
		}

		/// <summary>
		/// Generate calls to ScriptRuntime.AddInstructionCount to keep track of
		/// executed instructions and call <code>ObserveInstructionCount()</code>
		/// if a threshold is exceeded.<br />
		/// Takes the count as a parameter - used to add monitoring to loops and
		/// other blocks that don't have any ops - this allows
		/// for monitoring/killing of while(true) loops and such.
		/// </summary>
		/// <remarks>
		/// Generate calls to ScriptRuntime.AddInstructionCount to keep track of
		/// executed instructions and call <code>ObserveInstructionCount()</code>
		/// if a threshold is exceeded.<br />
		/// Takes the count as a parameter - used to add monitoring to loops and
		/// other blocks that don't have any ops - this allows
		/// for monitoring/killing of while(true) loops and such.
		/// </remarks>
		private static void AddInstructionCount(ILGenerator il, int count)
		{
			il.Emit(OpCodes.Ldarg_1);
			il.EmitLoadConstant(count);
			AddScriptRuntimeInvoke(il, "AddInstructionCount", typeof (Context), typeof (int));
		}

		private void VisitIncDec(ILGenerator il, Node node)
		{
			var incrDecrMask = node.GetExistingIntProp(Node.INCRDECR_PROP);
			var child = node.GetFirstChild();
			switch (child.GetType())
			{
				case Token.GETVAR:
				{
					if (!hasVarsInRegs)
					{
						Kit.CodeBug();
					}
					var post = ((incrDecrMask & Node.POST_FLAG) != 0);
					var varIndex = fnCurrent.GetVarIndex(child);
					var reg = varRegisters[varIndex];
					var varIsDirectCallParameter = VarIsDirectCallParameter(varIndex);
					if (node.GetIntProp(Node.ISNUMBER_PROP, -1) != -1)
					{
						if (varIsDirectCallParameter)
						{
							reg.EmitLoadSlot2(il);
						}
						else
						{
							reg.EmitLoadSlot1(il);
						}
						if (post)
						{
							il.Emit(OpCodes.Dup);
						}
						il.EmitLoadConstant(1.0);
						if ((incrDecrMask & Node.DECR_FLAG) == 0)
						{
							il.Emit(OpCodes.Add);
						}
						else
						{
							il.Emit(OpCodes.Sub);
						}
						if (!post)
						{
							il.Emit(OpCodes.Dup);
						}
						if (varIsDirectCallParameter)
						{
							reg.EmitStoreSlot2(il);
						}
						else
						{
							reg.EmitStoreSlot1(il);
						}
					}
					else
					{
						if (varIsDirectCallParameter)
						{
							DcpLoadAsObject(il, reg);
						}
						else
						{
							reg.EmitLoadSlot1(il);
						}
						if (post)
						{
							il.Emit(OpCodes.Dup);
						}
						AddObjectToNumberUnBoxed(il);
						il.EmitLoadConstant(1.0);
						if ((incrDecrMask & Node.DECR_FLAG) == 0)
						{
							il.Emit(OpCodes.Add);
						}
						else
						{
							il.Emit(OpCodes.Sub);
						}
						il.Emit(OpCodes.Box, typeof (double));
						if (!post)
						{
							il.Emit(OpCodes.Dup);
						}
						reg.EmitStoreSlot1(il);
						break;
					}
					break;
				}

				case Token.NAME:
				{
					il.Emit(OpCodes.Ldarg_2);
					string k = child.GetString();
					il.EmitLoadConstant(k);
					// push name
					il.Emit(OpCodes.Ldarg_1);
					il.EmitLoadConstant(incrDecrMask);
					AddScriptRuntimeInvoke(il, "NameIncrDecr", typeof (Scriptable), typeof (String), typeof (Context), typeof (int));
					break;
				}

				case Token.GETPROPNOWARN:
				{
					throw Kit.CodeBug();
				}

				case Token.GETPROP:
				{
					var getPropChild = child.GetFirstChild();
					GenerateExpression(il, getPropChild, node);
					GenerateExpression(il, getPropChild.GetNext(), node);
					il.Emit(OpCodes.Ldarg_1);
					il.EmitLoadConstant(incrDecrMask);
					AddScriptRuntimeInvoke(il, "PropIncrDecr", typeof (Object), typeof (String), typeof (Context), typeof (int));
					break;
				}

				case Token.GETELEM:
				{
					var elemChild = child.GetFirstChild();
					GenerateExpression(il, elemChild, node);
					GenerateExpression(il, elemChild.GetNext(), node);
					il.Emit(OpCodes.Ldarg_1);
					il.EmitLoadConstant(incrDecrMask);
					if (elemChild.GetNext().GetIntProp(Node.ISNUMBER_PROP, -1) != -1)
					{
						AddOptRuntimeInvoke(il, "ElemIncrDecr", typeof (object), typeof (double), typeof (Context), typeof (int));
					}
					else
					{
						AddScriptRuntimeInvoke(il, "ElemIncrDecr", typeof (object), typeof (Object), typeof (Context), typeof (int));
					}
					break;
				}

				case Token.GET_REF:
				{
					var refChild = child.GetFirstChild();
					GenerateExpression(il, refChild, node);
					il.Emit(OpCodes.Ldarg_1);
					il.EmitLoadConstant(incrDecrMask);
					AddScriptRuntimeInvoke(il, "RefIncrDecr", typeof (Ref), typeof (Context), typeof (int));
					break;
				}

				default:
				{
					Codegen.BadTree();
					break;
				}
			}
		}

		private static bool IsArithmeticNode(Node node)
		{
			var type = node.GetType();
			return (type == Token.SUB) || (type == Token.MOD) || (type == Token.DIV) || (type == Token.MUL);
		}

		private void VisitArithmetic(Node node, OpCode opCode, Node child, Node parent, ILGenerator il)
		{
			var childNumberFlag = node.GetIntProp(Node.ISNUMBER_PROP, -1);
			if (childNumberFlag != -1)
			{
				GenerateExpression(il, child, node);
				GenerateExpression(il, child.GetNext(), node);
				il.Emit(opCode);
			}
			else
			{
				var childOfArithmetic = IsArithmeticNode(parent);
				GenerateExpression(il, child, node);
				if (!IsArithmeticNode(child))
				{
					AddObjectToNumberUnBoxed(il);
				}
				else
				{
					il.Emit(OpCodes.Unbox_Any, typeof (double));
				}
				GenerateExpression(il, child.GetNext(), node);
				if (!IsArithmeticNode(child.GetNext()))
				{
					AddObjectToNumberUnBoxed(il);
				}
				else
				{
					il.Emit(OpCodes.Unbox_Any, typeof (double));
				}
				il.Emit(opCode);
				if (!childOfArithmetic)
				{
				}
			}
			il.Emit(OpCodes.Conv_R8);
			il.Emit(OpCodes.Box, typeof(double));
		}

		private void VisitBitOp(ILGenerator il, Node node, int type, Node child)
		{
			var childNumberFlag = node.GetIntProp(Node.ISNUMBER_PROP, -1);
			GenerateExpression(il, child, node);
			// special-case URSH; work with the target arg as a long, so
			// that we can return a 32-bit unsigned value, and call
			// toUint32 instead of toInt32.
			if (type == Token.URSH)
			{
				AddScriptRuntimeInvoke(il, "ToUInt32", typeof (Object));
				GenerateExpression(il, child.GetNext(), node);
				AddScriptRuntimeInvoke(il, "ToInt32", typeof (Object));
				// Looks like we need to explicitly mask the shift to 5 bits -
				// LUSHR takes 6 bits.
				il.EmitLoadConstant(31);
				il.Emit(OpCodes.And);
				il.Emit(OpCodes.Shr_Un);
				il.Emit(OpCodes.Conv_R8);
				il.Emit(OpCodes.Box, typeof (double));
				return;
			}
			if (childNumberFlag == -1)
			{
				AddScriptRuntimeInvoke(il, "ToInt32", typeof (Object));
				GenerateExpression(il, child.GetNext(), node);
				AddScriptRuntimeInvoke(il, "ToInt32", typeof (Object));
			}
			else
			{
				AddScriptRuntimeInvoke(il, "ToInt32", typeof (double));
				GenerateExpression(il, child.GetNext(), node);
				AddScriptRuntimeInvoke(il, "ToInt32", typeof (double));
			}
			switch (type)
			{
				case Token.BITOR:
				{
					il.Emit(OpCodes.Or);
					break;
				}

				case Token.BITXOR:
				{
					il.Emit(OpCodes.Xor);
					break;
				}

				case Token.BITAND:
				{
					il.Emit(OpCodes.And);
					break;
				}

				case Token.RSH:
				{
					il.Emit(OpCodes.Shr);
					break;
				}

				case Token.LSH:
				{
					il.Emit(OpCodes.Shl);
					break;
				}

				default:
				{
					throw Codegen.BadTree();
				}
			}
			il.Emit(OpCodes.Conv_R8);
			il.Emit(OpCodes.Box, typeof (double));
			if (childNumberFlag == -1)
			{
			}
		}

		private IVariableInfoEmitter NodeIsDirectCallParameter(Node node)
		{
			if (node.GetType() == Token.GETVAR && inDirectCallFunction && !itsForcedObjectParameters)
			{
				var varIndex = fnCurrent.GetVarIndex(node);
				if (fnCurrent.IsParameter(varIndex))
				{
					return varRegisters[varIndex];
				}
			}
			return null;
		}

		private bool VarIsDirectCallParameter(int varIndex)
		{
			return fnCurrent.IsParameter(varIndex) && inDirectCallFunction && !itsForcedObjectParameters;
		}

		private static void GenSimpleCompare(ILGenerator il, int type, Label? trueGOTO, Label? falseGOTO)
		{
			if (trueGOTO == null)
			{
				throw Codegen.BadTree();
			}
			switch (type)
			{
				case Token.LE:
				{
					il.Emit(OpCodes.Ble, trueGOTO.Value);
					break;
				}

				case Token.GE:
				{
					il.Emit(OpCodes.Bge, trueGOTO.Value);
					break;
				}

				case Token.LT:
				{
					il.Emit(OpCodes.Blt, trueGOTO.Value);
					break;
				}

				case Token.GT:
				{
					il.Emit(OpCodes.Bgt, trueGOTO.Value);
					break;
				}

				default:
				{
					throw Codegen.BadTree();
				}
			}
			if (falseGOTO != null)
			{
				il.Emit(OpCodes.Br, falseGOTO.Value);
			}
		}

		private void VisitIfJumpRelOp(ILGenerator il, Node node, Node child, Label? trueGOTO, Label? falseGOTO)
		{
			if (trueGOTO == null || falseGOTO == null)
			{
				throw Codegen.BadTree();
			}
			var type = node.GetType();
			var rChild = child.GetNext();
			var ifne = OpCodes.Brtrue;
			var @goto = OpCodes.Br;
			if (type == Token.INSTANCEOF || type == Token.IN)
			{
				GenerateExpression(il, child, node);
				GenerateExpression(il, rChild, node);
				il.Emit(OpCodes.Ldarg_1);
				AddScriptRuntimeInvoke(il, (type == Token.INSTANCEOF) ? "InstanceOf" : "In", typeof (Object), typeof (Object), typeof (Context));
				il.Emit(ifne, trueGOTO.Value);
				il.Emit(@goto, falseGOTO.Value);
				return;
			}
			var childNumberFlag = node.GetIntProp(Node.ISNUMBER_PROP, -1);
			var left_dcp_register = NodeIsDirectCallParameter(child);
			var right_dcp_register = NodeIsDirectCallParameter(rChild);
			if (childNumberFlag != -1)
			{
				// Force numeric context on both parameters and optimize
				// direct call case as Optimizer currently does not handle it
				if (childNumberFlag != Node.RIGHT)
				{
					// Left already has number content
					GenerateExpression(il, child, node);
				}
				else
				{
					if (left_dcp_register != null)
					{
						DcpLoadAsNumber(il, left_dcp_register);
					}
					else
					{
						GenerateExpression(il, child, node);
						AddObjectToNumber(il);
					}
				}
				if (childNumberFlag != Node.LEFT)
				{
					// Right already has number content
					GenerateExpression(il, rChild, node);
				}
				else
				{
					if (right_dcp_register != null)
					{
						DcpLoadAsNumber(il, right_dcp_register);
					}
					else
					{
						GenerateExpression(il, rChild, node);
						AddObjectToNumber(il);
					}
				}
				GenSimpleCompare(il, type, trueGOTO, falseGOTO);
			}
			else
			{
				if (left_dcp_register != null && right_dcp_register != null)
				{
					// Generate code to dynamically check for number content
					// if both operands are dcp
					var leftIsNotNumber = il.DefineLabel();
					left_dcp_register.EmitLoadSlot1(il);
					il.Emit(OpCodes.Ldtoken, typeof (void));
					il.Emit(OpCodes.Ceq);
					il.Emit(OpCodes.Brfalse, leftIsNotNumber);
					left_dcp_register.EmitLoadSlot2(il);
					DcpLoadAsNumber(il, right_dcp_register);
					GenSimpleCompare(il, type, trueGOTO, falseGOTO);
					il.MarkLabel(leftIsNotNumber);
					var rightIsNotNumber = il.DefineLabel();
					right_dcp_register.EmitLoadSlot1(il);
					il.Emit(OpCodes.Ldtoken, typeof(void));
					il.Emit(OpCodes.Ceq);
					il.Emit(OpCodes.Brfalse, rightIsNotNumber);
					left_dcp_register.EmitLoadSlot1(il);
					AddObjectToNumber(il);
					right_dcp_register.EmitLoadSlot2(il);
					GenSimpleCompare(il, type, trueGOTO, falseGOTO);
					il.MarkLabel(rightIsNotNumber);
					// Load both register as objects to call generic cmp_*
					left_dcp_register.EmitLoadSlot1(il);
					right_dcp_register.EmitLoadSlot1(il);
				}
				else
				{
					GenerateExpression(il, child, node);
					GenerateExpression(il, rChild, node);
				}
				if (type == Token.GE || type == Token.GT)
				{
					var local1 = il.DeclareLocal(typeof (object));
					var local2 = il.DeclareLocal(typeof (object));
					il.EmitStoreLocal(local1);
					il.EmitStoreLocal(local2);
					il.EmitLoadLocal(local1);
					il.EmitLoadLocal(local2);
				}
				var routine = ((type == Token.LT) || (type == Token.GT)) ? "Cmp_LT" : "Cmp_LE";
				AddScriptRuntimeInvoke(il, routine, typeof (Object), typeof (Object));
				il.Emit(ifne, trueGOTO.Value);
				il.Emit(@goto, falseGOTO.Value);
			}
		}

		private void VisitIfJumpEqOp(ILGenerator il, Node node, Node child, Label? trueGOTO, Label? falseGOTO)
		{
			if (trueGOTO == null || falseGOTO == null)
			{
				throw Codegen.BadTree();
			}
			var type = node.GetType();
			var rChild = child.GetNext();
			// Optimize if one of operands is null
			if (child.GetType() == Token.NULL || rChild.GetType() == Token.NULL)
			{
				// eq is symmetric in this case
				if (child.GetType() == Token.NULL)
				{
					child = rChild;
				}
				GenerateExpression(il, child, node);
				if (type == Token.SHEQ || type == Token.SHNE)
				{
					var testCode = (type == Token.SHEQ) ? OpCodes.Brfalse : OpCodes.Brtrue;
					il.Emit(testCode, trueGOTO.Value);
				}
				else
				{
					if (type != Token.EQ)
					{
						// swap false/true targets for !=
						if (type != Token.NE)
						{
							throw Codegen.BadTree();
						}
						var tmp = trueGOTO;
						trueGOTO = falseGOTO;
						falseGOTO = tmp;
					}
					il.Emit(OpCodes.Dup);
					var undefCheckLabel = il.DefineLabel();
					il.Emit(OpCodes.Brtrue, undefCheckLabel);
					il.Emit(OpCodes.Pop);
					il.Emit(OpCodes.Br, trueGOTO.Value);
					il.MarkLabel(undefCheckLabel);
					Codegen.PushUndefined(il);
					il.Emit(OpCodes.Ceq);
					il.Emit(OpCodes.Brtrue, trueGOTO.Value);
				}
				il.Emit(OpCodes.Br, falseGOTO.Value);
			}
			else
			{
				var child_dcp_register = NodeIsDirectCallParameter(child);
				if (child_dcp_register != null && rChild.GetType() == Token.TO_OBJECT)
				{
					var convertChild = rChild.GetFirstChild();
					if (convertChild.GetType() == Token.NUMBER)
					{
						child_dcp_register.EmitLoadSlot1(il);
						il.Emit(OpCodes.Ldtoken, typeof (void));
						var notNumbersLabel = il.DefineLabel();
						il.Emit(OpCodes.Ceq);
						il.Emit(OpCodes.Brfalse, notNumbersLabel);
						child_dcp_register.EmitLoadSlot2(il);
						var k = convertChild.GetDouble();
						il.EmitLoadConstant(k);
						il.Emit(OpCodes.Ceq);
						il.Emit(type == Token.EQ ? OpCodes.Brfalse : OpCodes.Brtrue, trueGOTO.Value);
						il.Emit(OpCodes.Br, falseGOTO.Value);
						il.MarkLabel(notNumbersLabel);
					}
				}
				// fall thru into generic handling
				GenerateExpression(il, child, node);
				GenerateExpression(il, rChild, node);
				string name;
				OpCode testCode;
				switch (type)
				{
					case Token.EQ:
					{
						name = "Eq";
						testCode = OpCodes.Brtrue;
						break;
					}

					case Token.NE:
					{
						name = "Eq";
						testCode = OpCodes.Brfalse;
						break;
					}

					case Token.SHEQ:
					{
						name = "ShallowEq";
						testCode = OpCodes.Brtrue;
						break;
					}

					case Token.SHNE:
					{
						name = "ShallowEq";
						testCode = OpCodes.Brfalse;
						break;
					}

					default:
					{
						throw Codegen.BadTree();
					}
				}
				AddScriptRuntimeInvoke(il, name, typeof (Object), typeof (Object));
				il.Emit(testCode, trueGOTO.Value);
				il.Emit(OpCodes.Br, falseGOTO.Value);
			}
		}

		private void VisitSetName(ILGenerator il, Node node, Node child)
		{
			var name = node.GetFirstChild().GetString();
			while (child != null)
			{
				GenerateExpression(il, child, node);
				child = child.GetNext();
			}
			il.Emit(OpCodes.Ldarg_1);
			il.Emit(OpCodes.Ldarg_2);
			il.EmitLoadConstant(name);
			AddScriptRuntimeInvoke(il, "SetName", typeof (Scriptable), typeof (Object), typeof (Context), typeof (Scriptable), typeof (String));
		}

		private void VisitStrictSetName(ILGenerator il, Node node, Node child)
		{
			var name = node.GetFirstChild().GetString();
			while (child != null)
			{
				GenerateExpression(il, child, node);
				child = child.GetNext();
			}
			il.Emit(OpCodes.Ldarg_1);
			il.Emit(OpCodes.Ldarg_2);
			il.EmitLoadConstant(name);
			AddScriptRuntimeInvoke(il, "StrictSetName", typeof (Scriptable), typeof (Object), typeof (Context), typeof (Scriptable), typeof (String));
		}

		private void VisitSetConst(ILGenerator il, Node node, Node child)
		{
			var name = node.GetFirstChild().GetString();
			while (child != null)
			{
				GenerateExpression(il, child, node);
				child = child.GetNext();
			}
			il.Emit(OpCodes.Ldarg_1);
			il.EmitLoadConstant(name);
			AddScriptRuntimeInvoke(il, "SetConst", typeof (Scriptable), typeof (Object), typeof (Context), typeof (String));
		}

		private void VisitGetVar(ILGenerator il, Node node)
		{
			if (!hasVarsInRegs)
			{
				Kit.CodeBug();
			}
			var varIndex = fnCurrent.GetVarIndex(node);
			var reg = varRegisters[varIndex];
			if (VarIsDirectCallParameter(varIndex))
			{
				// Remember that here the isNumber flag means that we
				// want to use the incoming parameter in a Number
				// context, so test the object type and convert the
				//  value as necessary.
				if (node.GetIntProp(Node.ISNUMBER_PROP, -1) != -1)
				{
					DcpLoadAsNumber(il, reg);
				}
				else
				{
					DcpLoadAsObject(il, reg);
				}
			}
			else
			{
				reg.EmitLoadSlot1(il);
			}
		}

		private void VisitSetVar(ILGenerator il, Node node, Node child, bool needValue)
		{
			if (!hasVarsInRegs)
			{
				Kit.CodeBug();
			}
			var varIndex = fnCurrent.GetVarIndex(node);
			GenerateExpression(il, child.GetNext(), node);
			var isNumber = (node.GetIntProp(Node.ISNUMBER_PROP, -1) != -1);
			var reg = varRegisters[varIndex];
			var constDeclarations = fnCurrent.fnode.GetParamAndVarConst();
			if (constDeclarations[varIndex])
			{
				if (!needValue)
				{
					il.Emit(OpCodes.Pop);
				}
			}
			else
			{
				if (VarIsDirectCallParameter(varIndex))
				{
					if (isNumber)
					{
						if (needValue)
						{
							il.Emit(OpCodes.Dup);
						}
						reg.EmitLoadSlot1(il);
						il.Emit(OpCodes.Ldtoken, typeof(void));
						var isNumberLabel = il.DefineLabel();
						var beyond = il.DefineLabel();
						il.Emit(OpCodes.Beq, isNumberLabel);
						reg.EmitStoreSlot1(il);
						il.Emit(OpCodes.Br, beyond);
						il.MarkLabel(isNumberLabel);
						//itsStackTop = stackTop;
						reg.EmitStoreSlot2(il);
						il.MarkLabel(beyond);
					}
					else
					{
						if (needValue)
						{
							il.Emit(OpCodes.Dup);
						}
						reg.EmitStoreSlot1(il);
					}
				}
				else
				{
					var isNumberVar = fnCurrent.IsNumberVar(varIndex);
					if (isNumber)
					{
						if (isNumberVar)
						{
							reg.EmitStoreSlot1(il);
							if (needValue)
							{
								reg.EmitLoadSlot1(il);
							}
						}
						else
						{
							if (needValue)
							{
								il.Emit(OpCodes.Dup);
							}
							// Cannot save number in variable since !isNumberVar,
							// so convert to object
							reg.EmitStoreSlot1(il);
						}
					}
					else
					{
						if (isNumberVar)
						{
							Kit.CodeBug();
						}
						reg.EmitStoreSlot1(il);
						if (needValue)
						{
							reg.EmitLoadSlot1(il);
						}
					}
				}
			}
		}
		private void VisitSetConstVar(ILGenerator il, Node node, Node child, bool needValue)
		{
			if (!hasVarsInRegs)
			{
				Kit.CodeBug();
			}
			/* 
			 *  if (!regInitalized) {
			 *      regInitalized = 1;
			 *      reg = expression
			 *  }
			 *  #if (needValue) {
			 *      return reg;
			 *  }
			 */
			var varIndex = fnCurrent.GetVarIndex(node);
			GenerateExpression(il, child.GetNext(), node);
			var reg = varRegisters[varIndex];
			var beyond = il.DefineLabel();
			var noAssign = il.DefineLabel();
			reg.EmitLoadSlot2(il);
			il.Emit(OpCodes.Brtrue, noAssign);
			il.EmitLoadConstant(1);
			reg.EmitStoreSlot2(il);
			reg.EmitStoreSlot1(il);
			if (needValue)
			{
				reg.EmitLoadSlot1(il);
				il.MarkLabel(noAssign);
			}
			else
			{
				il.Emit(OpCodes.Br, beyond);
				il.MarkLabel(noAssign);
				il.Emit(OpCodes.Pop);
			}
			il.MarkLabel(beyond);
		}

		private void VisitGetProp(ILGenerator il, Node node, Node child)
		{
			GenerateExpression(il, child, node);
			// object
			var nameChild = child.GetNext();
			GenerateExpression(il, nameChild, node);
			// the name
			if (node.GetType() == Token.GETPROPNOWARN)
			{
				il.Emit(OpCodes.Ldarg_1);
				AddScriptRuntimeInvoke(il, "GetObjectPropNoWarn", typeof (Object), typeof (String), typeof (Context));
				return;
			}
			var childType = child.GetType();
			if (childType == Token.THIS && nameChild.GetType() == Token.STRING)
			{
				il.Emit(OpCodes.Ldarg_1);
				AddScriptRuntimeInvoke(il, "GetObjectProp", typeof (Scriptable), typeof (String), typeof (Context));
			}
			else
			{
				il.Emit(OpCodes.Ldarg_1);
				il.Emit(OpCodes.Ldarg_2);
				AddScriptRuntimeInvoke(il, "GetObjectProp", typeof (Object), typeof (String), typeof (Context), typeof (Scriptable));
			}
		}

		private void VisitSetProp(ILGenerator il, int type, Node node, Node child)
		{
			var objectChild = child;
			GenerateExpression(il, child, node);
			child = child.GetNext();
			if (type == Token.SETPROP_OP)
			{
				il.Emit(OpCodes.Dup);
			}
			var nameChild = child;
			GenerateExpression(il, child, node);
			child = child.GetNext();
			if (type == Token.SETPROP_OP)
			{
				// stack: ... object object name -> ... object name object name
				il.EmitDupX1();
				//for 'this.foo += ...' we call thisGet which can skip some
				//casting overhead.
				if (objectChild.GetType() == Token.THIS && nameChild.GetType() == Token.STRING)
				{
					il.Emit(OpCodes.Ldarg_1);
					AddScriptRuntimeInvoke(il, "GetObjectProp", typeof (Scriptable), typeof (String), typeof (Context));
				}
				else
				{
					il.Emit(OpCodes.Ldarg_1);
					AddScriptRuntimeInvoke(il, "GetObjectProp", typeof (Object), typeof (String), typeof (Context));
				}
			}
			GenerateExpression(il, child, node);
			il.Emit(OpCodes.Ldarg_1);
			AddScriptRuntimeInvoke(il, "SetObjectProp", typeof (Object), typeof (String), typeof (Object), typeof (Context));
		}

		private void VisitSetElem(ILGenerator il, int type, Node node, Node child)
		{
			GenerateExpression(il, child, node);
			child = child.GetNext();
			if (type == Token.SETELEM_OP)
			{
				il.Emit(OpCodes.Dup);
			}
			GenerateExpression(il, child, node);
			child = child.GetNext();
			var indexIsNumber = (node.GetIntProp(Node.ISNUMBER_PROP, -1) != -1);
			if (type == Token.SETELEM_OP)
			{
				if (indexIsNumber)
				{
					// stack: ... object object number
					//        -> ... object number object number
					il.Emit(ByteCode.DUP2_X1);
					il.Emit(OpCodes.Ldarg_1);
					AddOptRuntimeInvoke(il, "GetObjectIndex", typeof (object), typeof (double), typeof (Context));
				}
				else
				{
					// stack: ... object object indexObject
					//        -> ... object indexObject object indexObject
					il.EmitDupX1();
					il.Emit(OpCodes.Ldarg_1);
					AddScriptRuntimeInvoke(il, "GetObjectElem", typeof (Object), typeof (Object), typeof (Context));
				}
			}
			GenerateExpression(il, child, node);
			il.Emit(OpCodes.Ldarg_1);
			if (indexIsNumber)
			{
				AddScriptRuntimeInvoke(il, "SetObjectIndex", typeof (Object), typeof (Double), typeof (Object), typeof (Context));
			}
			else
			{
				AddScriptRuntimeInvoke(il, "SetObjectElem", typeof (Object), typeof (Object), typeof (Object), typeof (Context));
			}
		}

		private void VisitDotQuery(ILGenerator il, Node node, Node child)
		{
			UpdateLineNumber(node, il);
			GenerateExpression(il, child, node);
			il.Emit(OpCodes.Ldarg_2);
			AddScriptRuntimeInvoke(il, "EnterDotQuery", typeof (Object), typeof (Scriptable));
			il.EmitStoreArgument(2);
			// add push null/pop with label in between to simplify code for loop
			// continue when it is necessary to pop the null result from
			// updateDotQuery
			il.Emit(OpCodes.Ldnull);
			var queryLoopStart = il.DefineLabel();
			il.MarkLabel(queryLoopStart);
			// loop continue jumps here
			il.Emit(OpCodes.Pop);
			GenerateExpression(il, child.GetNext(), node);
			AddObjectToBoolean(il);
			il.Emit(OpCodes.Ldarg_2);
			AddScriptRuntimeInvoke(il, "UpdateDotQuery", typeof (bool), typeof (Scriptable));
			il.Emit(OpCodes.Dup);
			il.Emit(OpCodes.Brfalse, queryLoopStart);
			// stack: ... non_null_result_of_updateDotQuery
			il.Emit(OpCodes.Ldarg_2);
			AddScriptRuntimeInvoke(il, "LeaveDotQuery", typeof (Scriptable));
			il.EmitStoreArgument(2);
		}

		private static void AddObjectToBoolean(ILGenerator il)
		{
			AddScriptRuntimeInvoke(il, "ToBoolean", typeof (Object));
			il.Emit(OpCodes.Box, typeof (bool));
		}

        private static LocalBuilder GetLocalBlockRegister(Node node)
		{
			var localBlock = (Node)node.GetProp(Node.LOCAL_BLOCK_PROP);
            return (LocalBuilder) localBlock.GetExistingProp(Node.LOCAL_PROP);
		}

		private static void DcpLoadAsNumber(ILGenerator il, IVariableInfoEmitter reg)
		{
			/*
			 *  if (reg[0] == typeof(void)) {
			 *      return ToNumber(reg[0]);
			 *  } else {
			 *      return reg[1];
			 *  }
			 */
			reg.EmitLoadSlot1(il);
			il.Emit(OpCodes.Ldtoken, typeof(void));
			var isNumberLabel = il.DefineLabel();
			il.Emit(OpCodes.Ceq);
			il.Emit(OpCodes.Brtrue, isNumberLabel);
			reg.EmitLoadSlot1(il);
			AddObjectToNumber(il);
			var beyond = il.DefineLabel();
			il.Emit(OpCodes.Br, beyond);
			il.MarkLabel(isNumberLabel);
			reg.EmitLoadSlot2(il);
			il.MarkLabel(beyond);
		}

		private static void DcpLoadAsObject(ILGenerator il, IVariableInfoEmitter reg)
		{
			/*
			 *  if (reg[0] == typeof(void)) {
			 *      return reg[0];
			 *  } else {
			 *      return (object) reg[1];
			 *  }
			 */ 
			reg.EmitLoadSlot1(il);
			il.Emit(OpCodes.Ldtoken, typeof(void));
			var isNumberLabel = il.DefineLabel();
			il.Emit(OpCodes.Ceq);
			il.Emit(OpCodes.Brtrue, isNumberLabel);
			reg.EmitLoadSlot1(il);
			var beyond = il.DefineLabel();
			il.Emit(OpCodes.Br, beyond);
			il.MarkLabel(isNumberLabel);
			reg.EmitLoadSlot2(il);
			il.Emit(OpCodes.Box, typeof (double));
			il.MarkLabel(beyond);
		}

		private void AddGoto(ILGenerator il, OpCode opcode, Node target)
		{
			var targetLabel = GetTargetLabel(il, target);
			il.Emit(opcode, targetLabel);
		}

		private static void AddObjectToNumber(ILGenerator il)
		{
			AddScriptRuntimeInvoke(il, "ToNumber", typeof(object));
			il.Emit(OpCodes.Box, typeof(double));
		}

		private static void AddObjectToNumberUnBoxed(ILGenerator il)
		{
			AddScriptRuntimeInvoke(il, "ToNumber", typeof(object));
		}

		private void AddNewObjectArray(ILGenerator il, int size)
		{
			if (size == 0)
			{
				if (itsZeroArgArray != null)
				{
					il.EmitLoadLocal(itsZeroArgArray);
				}
				else
				{
					il.Emit(OpCodes.Ldsfld, typeof(ScriptRuntime).GetField("emptyArgs"));
				}
			}
			else
			{
				il.EmitLoadConstant(size);
				il.Emit(OpCodes.Newarr, typeof (Object));
			}
		}

		private static void AddScriptRuntimeInvoke(ILGenerator il, string methodName, params Type[] types)
		{
			il.Emit(OpCodes.Call, typeof (ScriptRuntime).GetMethod(methodName, types));
		}

		private static void AddOptRuntimeInvoke(ILGenerator il, string methodName, params Type[] types)
		{
			il.Emit(OpCodes.Call, typeof (OptRuntime).GetMethod(methodName, types));
		}

		private static void AddJumpedBooleanWrap(ILGenerator il, Label trueLabel, Label falseLabel)
		{
			il.MarkLabel(falseLabel);
			var skip = il.DefineLabel();
			il.EmitLoadConstant(false);
			il.Emit(OpCodes.Box, typeof(bool));
			il.Emit(OpCodes.Br, skip);
			il.MarkLabel(trueLabel);
			il.EmitLoadConstant(true);
			il.Emit(OpCodes.Box, typeof(bool));
			il.MarkLabel(skip);
		}

	    private readonly List<LocalBuilder> locals2 = new List<LocalBuilder>();

		private LocalBuilder DeclareLocal(ILGenerator il)
		{
		    var local = il.DeclareLocal(typeof (object));
		    locals2.Add(local);
		    return local;
		}

	    private void ReleaseLocal(LocalBuilder local)
	    {
	        locals2.Remove(local);
	    }

	    // This is a valid call only for a local that is allocated by default.
		private void IncReferenceWordLocal(int local)
		{
			locals[local]++;
		}

		// This is a valid call only for a local that is allocated by default.
		private void DecReferenceWordLocal(int local)
		{
			locals[local]--;
		}

		internal const int GENERATOR_TERMINATE = -1;

		internal const int GENERATOR_START = 0;

		internal const int GENERATOR_YIELD_START = 1;

		internal ClassFileWriter cfw;

		internal Codegen codegen;

		internal CompilerEnvirons compilerEnv;

		internal ScriptNode scriptOrFn;

		public int scriptOrFnIndex;

		private int savedCodeOffset;

		private OptFunctionNode fnCurrent;

		private const int MAX_LOCALS = 1024;

		private int[] locals;

		private short firstFreeLocal;

		private short localsMax;

		private int itsLineNumber;

		private bool hasVarsInRegs;

		private IVariableInfoEmitter[] varRegisters;

		private short[] argRegisters;

		private bool inDirectCallFunction;

		private bool itsForcedObjectParameters;

		private Label? enterAreaStartLabel;

		private Label? epilogueLabel;

		private bool inLocalBlock;

		private LocalBuilder popvLocal;

		private short argsArgument;

		private int operationLocal;

		private short thisObjLocal;

		private LocalBuilder itsZeroArgArray;

		private LocalBuilder itsOneArgArray;

		private LocalBuilder generatorStateLocal;

		public bool isGenerator;

		private Label[] generatorSwitch;

		private int maxLocals;

		private int maxStack;

		private IDictionary<Node, FinallyReturnPoint> finallys;

		public CachingTypeBuilder tb;
		public ConstructorInfo constructor;
		public MethodInfo regExpInit;
		public IdentityGenerator identityGenerator;

		internal class FinallyReturnPoint
		{
			public readonly IList<Label> jsrPoints = new List<Label>();

			public Label tableLabel;
			// special known locals. If you add a new local here, be sure
			// to initialize it to -1 in initBodyGeneration
		}

		public BodyCodegen()
		{
			exceptionManager = new ExceptionManager(this);
		}

		internal interface IVariableInfoEmitter
		{
			void EmitLoadSlot1(ILGenerator il);
			void EmitStoreSlot1(ILGenerator il);
			void EmitLoadSlot2(ILGenerator il);
			void EmitStoreSlot2(ILGenerator il);
			void SetLocalInfo(string name);
		}

		private sealed class ArgumentInfoEmitter : IVariableInfoEmitter
		{
			private readonly int slot1;
			private readonly int slot2;

			public ArgumentInfoEmitter(int slot1, int slot2)
			{
				this.slot1 = slot1;
				this.slot2 = slot2;
			}

			public void EmitLoadSlot1(ILGenerator il)
			{
				il.EmitLoadArgument(slot1);
			}

			public void EmitStoreSlot1(ILGenerator il)
			{
				il.EmitStoreArgument(slot1);
			}

			public void EmitLoadSlot2(ILGenerator il)
			{
				il.EmitLoadArgument(slot2);
			}

			public void EmitStoreSlot2(ILGenerator il)
			{
				il.EmitStoreArgument(slot2);
			}

			public void SetLocalInfo(string name)
			{
			}
		}

		private sealed class LocalInfoEmitter : IVariableInfoEmitter
		{
			private readonly LocalBuilder slot1;
			private readonly LocalBuilder slot2;

			public LocalInfoEmitter(LocalBuilder slot1, LocalBuilder slot2)
			{
				this.slot1 = slot1;
				this.slot2 = slot2;
			}

			public void EmitLoadSlot1(ILGenerator il)
			{
				il.EmitLoadLocal(slot1);
			}

			public void EmitStoreSlot1(ILGenerator il)
			{
				il.EmitStoreLocal(slot1);
			}

			public void EmitLoadSlot2(ILGenerator il)
			{
				il.EmitLoadLocal(slot2);
			}

			public void EmitStoreSlot2(ILGenerator il)
			{
				il.EmitStoreLocal(slot1);
			}

			public void SetLocalInfo(string name)
			{
				slot1.SetLocalSymInfo(name);
			}
		}

		public static BodyCodegen CreateBodyCodegen(Codegen codegen, ScriptNode n, int i, ConstructorInfo constructor, MethodInfo regExpInit, CachingTypeBuilder tb, CompilerEnvirons compilerEnv, bool isGenerator)
		{
			var bodygen = new BodyCodegen
			{
				tb = tb,
				codegen = codegen,
				compilerEnv = compilerEnv,
				scriptOrFn = n,
				scriptOrFnIndex = i,
				isGenerator = isGenerator,
				constructor = constructor,
				regExpInit = regExpInit,
				identityGenerator = new IdentityGenerator(),
			};
			bodygen.InitBodyGeneration();
			return bodygen;
		}
	}
}
#endif