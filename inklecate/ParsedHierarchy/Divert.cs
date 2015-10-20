﻿using System.Collections.Generic;
using System.Linq;

namespace Inklewriter.Parsed
{
	internal class Divert : Parsed.Object
	{
		public Parsed.Path target { get; protected set; }
        public Parsed.Object targetContent { get; protected set; }
        public List<Expression> arguments { get; protected set; }
		public Runtime.Divert runtimeDivert { get; protected set; }
        public bool isFunctionCall { get; set; }
        public bool isToGather { get; set; }
        public bool isTunnel { get; set; }
        public bool isPaste { get; set; }
        public bool isEnd { 
            get {
                return target != null && target.dotSeparatedComponents == "END";
            }
        }
        public bool isDone { 
            get {
                return target != null && target.dotSeparatedComponents == "DONE";
            }
        }

        public Divert (Parsed.Path target, List<Expression> arguments = null)
		{
			this.target = target;
            this.arguments = arguments;

            if (arguments != null) {
                AddContent (arguments.Cast<Parsed.Object> ().ToList ());
            }
		}

        public Divert (Parsed.Object targetContent)
        {
            this.targetContent = targetContent;
        }

		public override Runtime.Object GenerateRuntimeObject ()
		{
            if (isEnd) {
                return Runtime.ControlCommand.Stop ();
            }

            // Done = return from paste
            if (isDone) {
                return new Runtime.PushPop (
                    Runtime.PushPop.Type.Paste, 
                    Runtime.PushPop.Direction.Pop);
            }

            runtimeDivert = new Runtime.Divert ();

            // Normally we resolve the target content during the
            // Resolve phase, since we expect all runtime objects to
            // be available in order to find the final runtime path for
            // the destination. However, we need to resolve the target
            // (albeit without the runtime target) early so that
            // we can get information about the arguments - whether
            // they're by reference - since it affects the code we 
            // generate here.
            ResolveTargetContent ();


            CheckArgumentValidity ();

            // Passing arguments to the knot
            bool requiresArgCodeGen = arguments != null && arguments.Count > 0;
            if ( requiresArgCodeGen || isFunctionCall || isTunnel || isPaste ) {

                var container = new Runtime.Container ();

                // Generate code for argument evaluation
                // This argument generation is coded defensively - it should
                // attempt to generate the code for all the parameters, even if
                // they don't match the expected arguments. This is so that the
                // parameter objects themselves are generated correctly and don't
                // get into a state of attempting to resolve references etc
                // without being generated.
                if (requiresArgCodeGen) {

                    // Function calls already in an evaluation context
                    if (!isFunctionCall) {
                        container.AddContent (Runtime.ControlCommand.EvalStart());
                    }

                    List<FlowBase.Argument> targetArguments = null;
                    if( targetContent )
                        targetArguments = (targetContent as FlowBase).arguments;

                    for (var i = 0; i < arguments.Count; ++i) {
                        Expression argToPass = arguments [i];
                        FlowBase.Argument argExpected = null; 
                        if( targetArguments != null ) 
                            argExpected = targetArguments [i];

                        // Pass by reference: argument needs to be a variable reference
                        if (argExpected != null && argExpected.isByReference) {

                            var varRef = argToPass as VariableReference;
                            if (varRef == null) {
                                Error ("Expected variable name to pass by reference to 'ref " + argExpected.name + "' but saw " + argToPass.ToString ());
                                break;
                            }

                            var varPointer = new Runtime.LiteralVariablePointer (varRef.name);
                            container.AddContent (varPointer);
                        } 

                        // Normal value being passed: evaluate it as normal
                        else {
                            argToPass.GenerateIntoContainer (container);
                        }
                    }

                    // Function calls were already in an evaluation context
                    if (!isFunctionCall) {
                        container.AddContent (Runtime.ControlCommand.EvalEnd());
                    }
                }
                    
                // If this divert is a function call, tunnel or paste, we push to the call stack
                // so we can return again
                if (isFunctionCall || isTunnel || isPaste) {

                    Runtime.PushPop.Type pushPopType;

                    if (isFunctionCall)
                        pushPopType = Runtime.PushPop.Type.Function;
                    else if (isTunnel)
                        pushPopType = Runtime.PushPop.Type.Tunnel;
                    else
                        pushPopType = Runtime.PushPop.Type.Paste;

                    container.AddContent(new Runtime.PushPop(pushPopType, Runtime.PushPop.Direction.Push));
                }

                // Jump into the "function" (knot/stitch)
                container.AddContent (runtimeDivert);

                return container;
            } 

            // Simple divert
            else {
                return runtimeDivert;
            }			
		}


        // When the divert is to a target that's actually a variable name
        // rather than an explicit knot/stitch name, try interpretting it
        // as such by getting the variable name.
        public string PathAsVariableName()
        {
            return target.firstComponent;
        }
            

        void ResolveTargetContent()
        {
            if (isToGather || isEnd) {
                return;
            }

            if (targetContent == null) {

                // Is target of this divert a variable name that will be de-referenced
                // at runtime? If so, there won't be any further reference resolution
                // we can do at this point.
                var variableTargetName = PathAsVariableName ();
                if (variableTargetName != null) {
                    var flowBaseScope = ClosestFlowBase ();
                    if (flowBaseScope.ResolveVariableWithName (variableTargetName, fromNode:this)) {
                        runtimeDivert.variableDivertName = variableTargetName;
                        return;
                    }
                }

                targetContent = target.ResolveFromContext (this);
            }
        }

        public override void ResolveReferences(Story context)
		{
            if (isToGather || isEnd) {
                return;
            }

            if (targetContent) {
                runtimeDivert.targetPath = targetContent.runtimePath;
            }

            // Resolve children (the arguments)
            base.ResolveReferences (context);

            // May be null if it's a built in function (e.g. beats_since)
            var targetFlow = targetContent as FlowBase;
            if (targetFlow) {
                if (!targetFlow.isFunction && this.isFunctionCall) {
                    base.Error (targetFlow.name+" hasn't been marked as a function, but it's being called as one. Do you need to delcare the knot as '== function " + targetFlow.name + " =='?");
                } else if (targetFlow.isFunction && !this.isFunctionCall) {
                    base.Error (targetFlow.name+" can't be diverted to. It can only be called as a function since it's been marked as such: '" + targetFlow.name + "(...)'");
                }
            }
		}

        // Returns false if there's an error
        bool CheckArgumentValidity()
        {
            if (isToGather) 
                return true;

            // Argument passing: Check for errors in number of arguments
            var numArgs = 0;
            if (arguments != null && arguments.Count > 0)
                numArgs = arguments.Count;

            // Missing content?
            // Can't check arguments properly. It'll be due to some
            // other error though, so although there's a problem and 
            // we report false, we don't need to report a specific error.
            if (targetContent == null) {
                return false;
            }

            FlowBase targetFlow = targetContent as FlowBase;

            // No error, crikey!
            if (numArgs == 0 && (targetFlow == null || !targetFlow.hasParameters)) {
                return true;
            }

            if (targetFlow == null && numArgs > 0) {
                Error ("target needs to be a knot or stitch in order to pass arguments");
                return false;
            }

            if (targetFlow.arguments == null && numArgs > 0) {
                Error ("target (" + targetFlow.name + ") doesn't take parameters");
                return false;
            }

            var paramCount = targetFlow.arguments.Count;
            if (paramCount > 0 && this.parent is DivertTarget) {
                Error ("Can't store a link to a knot that takes parameters in a variable");
                return false;
            }

            if (paramCount != numArgs) {

                string butClause;
                if (numArgs == 0) {
                    butClause = "but there weren't any passed to it";
                } else if (numArgs < paramCount) {
                    butClause = "but only got " + numArgs;
                } else {
                    butClause = "but got " + numArgs;
                }
                Error ("to '" + targetFlow.name + "' requires " + paramCount + " arguments, "+butClause);
                return false;
            }
                
            if (targetFlow == null) {
                Error ("Can't call as a function or with arguments unless it's a knot or stitch");
                return false;
            }

            return true;
        }

        public override void Error (string message, Object source = null, bool isWarning = false)
        {
            // Could be getting an error from a nested Divert
            if (source != this && source) {
                base.Error (message, source);
                return;
            }

            if (isFunctionCall) {
                base.Error ("Function call " + message, source, isWarning);
            } else {
                base.Error ("Divert " + message, source, isWarning);
            }
        }

        public override string ToString ()
        {
            return target.ToString();
        }

	}
}

