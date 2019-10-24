﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Plang.Compiler.Backend.ASTExt;
using Plang.Compiler.TypeChecker;
using Plang.Compiler.TypeChecker.AST;
using Plang.Compiler.TypeChecker.AST.Declarations;
using Plang.Compiler.TypeChecker.AST.Expressions;
using Plang.Compiler.TypeChecker.AST.Statements;
using Plang.Compiler.TypeChecker.Types;

namespace Plang.Compiler.Backend.Symbolic
{
    class SymbolicCodeGenerator : ICodeGenerator
    {
        public IEnumerable<CompiledFile> GenerateCode(ICompilationJob job, Scope globalScope)
        {
            var context = new CompilationContext(job);
            var javaSource = GenerateSource(context, globalScope);
            return new List<CompiledFile> { javaSource };
        }

        private CompiledFile GenerateSource(CompilationContext context, Scope globalScope)
        {
            var source = new CompiledFile(context.FileName);

            WriteSourcePrologue(context, source.Stream);

            foreach (var decl in globalScope.AllDecls)
                WriteDecl(context, source.Stream, decl);

            WriteSourceEpilogue(context, source.Stream);

            return source;
        }

        private void WriteDecl(CompilationContext context, StringWriter output, IPDecl decl)
        {
            switch (decl)
            {
                case Function function:
                    if (function.IsForeign)
                        throw new NotImplementedException("Foreign functions are not yet supported");
                    
                    WriteFunction(context, output, function);
                    break;
                default:
                    context.WriteLine(output, $"// Skipping {decl.GetType().Name} '{decl.Name}'\n");
                    break;
            }
        }

        internal struct ControlFlowContext
        {
            internal readonly PathConstraintScope pcScope;
            internal readonly LoopScope? loopScope;
            internal readonly BranchScope? branchScope;

            public ControlFlowContext(PathConstraintScope pcScope, LoopScope? loopScope, BranchScope? branchScope)
            {
                this.pcScope = pcScope;
                this.loopScope = loopScope;
                this.branchScope = branchScope;
            }

            internal static ControlFlowContext FreshFuncContext(CompilationContext context)
            {
                return new ControlFlowContext(context.FreshPathConstraintScope(), null, null);
            }

            internal static ControlFlowContext FreshLoopContext(CompilationContext context)
            {
                return new ControlFlowContext(context.FreshPathConstraintScope(), context.FreshLoopScope(), null);
            }

            internal ControlFlowContext FreshBranchSubContext(CompilationContext context)
            {
                return new ControlFlowContext(context.FreshPathConstraintScope(), loopScope, context.FreshBranchScope());
            }
        }

        private void WriteFunction(CompilationContext context, StringWriter output, Function function)
        {
            if (function.Owner != null)
                throw new NotImplementedException("Non-static functions are not yet supported");

            if (function.CanReceive == true)
                throw new NotImplementedException("Async functions are not supported");

            var rootPCScope = context.FreshPathConstraintScope();

            var returnType = GetSymbolicType(function.Signature.ReturnType);
            var functionName = context.GetNameForDecl(function);

            context.WriteLine(output, $"static {returnType} ");
            context.Write(output, functionName);

            context.WriteLine(output, $"(");
            context.WriteLine(output, $"    Bdd {rootPCScope.PathConstraintVar},");

            for (int i = 0; i < function.Signature.Parameters.Count; i++)
            {
                var param = function.Signature.Parameters[i];
                context.Write(output, $"    {GetSymbolicType(param.Type, true)} {CompilationContext.GetVar(param.Name)}");
                if (i + 1 != function.Signature.Parameters.Count)
                    context.WriteLine(output, ",");
                else
                    context.WriteLine(output);
            }

            context.Write(output, ") ");

            context.WriteLine(output, "{");
            WriteFunctionBody(context, output, rootPCScope, function);
            context.WriteLine(output, "}");
            context.WriteLine(output);
        }

        private void WriteFunctionBody(CompilationContext context, StringWriter output, PathConstraintScope rootPCScope, Function function)
        {
            foreach (var local in function.LocalVariables)
            {
                context.WriteLine(output, $"{GetSymbolicType(local.Type)} {CompilationContext.GetVar(local.Name)} =");
                context.WriteLine(output, $"    {GetDefaultValue(context, rootPCScope, local.Type)};");
                context.WriteLine(output);
            }

            if (!function.Signature.ReturnType.IsSameTypeAs(PrimitiveType.Null))
            {
                context.WriteLine(output, $"java.util.List<{GetSymbolicType(function.Signature.ReturnType)}> {CompilationContext.ReturnValue} = {GetValueSummaryOps(context, function.Signature.ReturnType).GetName()}.empty();");
                context.WriteLine(output);
            }

            WriteStmt(function, context, output, ControlFlowContext.FreshFuncContext(context), function.Body);

            if (!function.Signature.ReturnType.IsSameTypeAs(PrimitiveType.Null))
            {
                context.WriteLine(output, $"return {CompilationContext.ReturnValue};");
            }
        }

        private bool CanEarlyReturn(IPStmt stmt)
        {
            switch (stmt)
            {
                case CompoundStmt compoundStmt:
                    return compoundStmt.Statements.Any((subStmt) => CanEarlyReturn(subStmt));
                case IfStmt ifStmt:
                    return CanEarlyReturn(ifStmt.ThenBranch) || CanEarlyReturn(ifStmt.ElseBranch);
                case WhileStmt whileStmt:
                    return CanEarlyReturn(whileStmt.Body);

                case GotoStmt _:
                case PopStmt _:
                case RaiseStmt _:
                case ReturnStmt _:
                    return true;
                
                default:
                    return false;
            }
        }

        private bool MustEarlyReturn(IPStmt stmt)
        {
            switch (stmt)
            {
                case CompoundStmt compoundStmt:
                    return compoundStmt.Statements.Any((subStmt) => MustEarlyReturn(subStmt));
                case IfStmt ifStmt:
                    return MustEarlyReturn(ifStmt.ThenBranch) && MustEarlyReturn(ifStmt.ElseBranch);
                case WhileStmt whileStmt:
                    return MustEarlyReturn(whileStmt.Body);

                case GotoStmt _:
                case PopStmt _:
                case RaiseStmt _:
                case ReturnStmt _:
                    return true;

                default:
                    return false;
            }
        }

        private bool CanJumpOut(IPStmt stmt)
        {
            switch (stmt)
            {
                case CompoundStmt compoundStmt:
                    return compoundStmt.Statements.Any((subStmt) => CanJumpOut(subStmt));
                case IfStmt ifStmt:
                    return CanJumpOut(ifStmt.ThenBranch) || CanJumpOut(ifStmt.ElseBranch);
                case WhileStmt whileStmt:
                    // Any breaks or continues inside this loop body will be "caught" by the loop,
                    // so we only want to consider statements which return from the entire function.
                    return CanEarlyReturn(whileStmt.Body);

                case GotoStmt _:
                case PopStmt _:
                case RaiseStmt _:
                case ReturnStmt _:
                case BreakStmt _:
                case ContinueStmt _:
                    return true;

                default:
                    return false;
            }
        }

        private bool MustJumpOut(IPStmt stmt)
        {
            switch (stmt)
            {
                case CompoundStmt compoundStmt:
                    return compoundStmt.Statements.Any((subStmt) => MustJumpOut(subStmt));
                case IfStmt ifStmt:
                    return MustJumpOut(ifStmt.ThenBranch) && MustJumpOut(ifStmt.ElseBranch);
                case WhileStmt whileStmt:
                    // Any breaks or continues inside this loop body will be "caught" by the loop,
                    // so we only want to consider statements which return from the entire function.
                    return MustEarlyReturn(whileStmt.Body);

                case GotoStmt _:
                case PopStmt _:
                case RaiseStmt _:
                case ReturnStmt _:
                case BreakStmt _:
                case ContinueStmt _:
                    return true;

                default:
                    return false;
            }
        }

        private void WriteStmt(Function function, CompilationContext context, StringWriter output, ControlFlowContext flowContext, IPStmt stmt)
        {
            switch (stmt)
            {
                case AssignStmt assignStmt:
                    if (!assignStmt.Value.Type.IsSameTypeAs(assignStmt.Location.Type))
                    {
                        throw new NotImplementedException($"Cannot yet handle assignment to variable of type {assignStmt.Location.Type.CanonicalRepresentation} from value of type {assignStmt.Value.Type.CanonicalRepresentation}");
                    }

                    WriteWithLValueMutationContext(
                        context,
                        output,
                        flowContext.pcScope,
                        assignStmt.Location,
                        false,
                        locationTemp =>
                        {
                            context.Write(output, $"{locationTemp} = ");
                            WriteExpr(context, output, flowContext.pcScope, assignStmt.Value);
                            context.WriteLine(output, ";");
                        }
                    );

                    break;

                case MoveAssignStmt moveStmt:
                    if (!moveStmt.FromVariable.Type.IsSameTypeAs(moveStmt.ToLocation.Type))
                    {
                        throw new NotImplementedException($"Cannot yet handle assignment to variable of type {moveStmt.ToLocation.Type.CanonicalRepresentation} from value of type {moveStmt.FromVariable.Type.CanonicalRepresentation}");
                    }

                    WriteWithLValueMutationContext(
                        context,
                        output,
                        flowContext.pcScope,
                        moveStmt.ToLocation,
                        false,
                        locationTemp =>
                        {
                            context.Write(output, $"{locationTemp} = ");
                            WriteExpr(context, output, flowContext.pcScope, new VariableAccessExpr(moveStmt.FromVariable.SourceLocation, moveStmt.FromVariable));
                            context.WriteLine(output, ";");
                        }
                    );

                    break;

                case ReturnStmt returnStmt:
                    if (!(returnStmt.ReturnValue is null))
                    {
                        var summaryOps = GetValueSummaryOps(context, returnStmt.ReturnValue.Type).GetName();

                        context.Write(output, $"{CompilationContext.ReturnValue} = {summaryOps}.merge2({CompilationContext.ReturnValue}, ");
                        WriteExpr(context, output, flowContext.pcScope, returnStmt.ReturnValue);
                        context.WriteLine(output, $");");
                    }

                    context.WriteLine(output, $"{flowContext.pcScope.PathConstraintVar} = {CompilationContext.BddLib}.constFalse();");

                    if (!(flowContext.loopScope is null))
                    {
                        context.WriteLine(output, $"{flowContext.loopScope.Value.LoopEarlyReturnFlag} = true;");
                    }

                    if (!(flowContext.branchScope is null))
                    {
                        context.WriteLine(output, $"{flowContext.branchScope.Value.JumpedOutFlag} = true;");
                    }

                    break;

                case BreakStmt breakStmt:
                    Debug.Assert(flowContext.loopScope.HasValue);
                    context.WriteLine(output, $"{flowContext.loopScope.Value.LoopExitsList}.add({flowContext.pcScope.PathConstraintVar});");

                    if (flowContext.branchScope.HasValue)
                    {
                        context.WriteLine(output, $"{flowContext.branchScope.Value.JumpedOutFlag} = true;");
                    }

                    context.WriteLine(output, $"{flowContext.pcScope.PathConstraintVar} = {CompilationContext.BddLib}.constFalse();");
                    break;

                case CompoundStmt compoundStmt:
                    // Used to deermine the number of closing braces to add at the end of the block
                    var nestedEarlyExitCheckCount = 0;

                    foreach (var subStmt in compoundStmt.Statements)
                    {
                        WriteStmt(function, context, output, flowContext, subStmt);
                        context.WriteLine(output);

                        if (MustJumpOut(subStmt))
                            break;

                        if (CanJumpOut(subStmt))
                        {
                            context.WriteLine(output, $"if (!{CompilationContext.BddLib}.isConstFalse({flowContext.pcScope.PathConstraintVar})) {{");
                            nestedEarlyExitCheckCount++;
                        }
                    }

                    for (var i = 0; i < nestedEarlyExitCheckCount; i++)
                    {
                        context.WriteLine(output, "}");
                    }

                    break;

                case WhileStmt whileStmt:
                    if (!(whileStmt.Condition is BoolLiteralExpr) && ((BoolLiteralExpr)whileStmt.Condition).Value)
                    {
                        throw new ArgumentOutOfRangeException("While statement condition should always be transformed to constant 'true' during IR simplification.");
                    }

                    ControlFlowContext loopContext = ControlFlowContext.FreshLoopContext(context);

                    /* Prologue */
                    context.WriteLine(output, $"java.util.List<Bdd> {loopContext.loopScope.Value.LoopExitsList} = new java.util.ArrayList<>();");
                    context.WriteLine(output, $"boolean {loopContext.loopScope.Value.LoopEarlyReturnFlag} = false;");
                    context.WriteLine(output, $"Bdd {loopContext.pcScope.PathConstraintVar} = {flowContext.pcScope.PathConstraintVar};");

                    /* Loop body */
                    context.WriteLine(output, $"while (!{CompilationContext.BddLib}.isConstFalse({loopContext.pcScope.PathConstraintVar})) {{");
                    WriteStmt(function, context, output, loopContext, whileStmt.Body);
                    context.WriteLine(output, "}");

                    /* Epilogue */
                    context.WriteLine(output, $"if ({loopContext.loopScope.Value.LoopEarlyReturnFlag}) {{");
                    context.WriteLine(output, $"{flowContext.pcScope.PathConstraintVar} = {CompilationContext.BddLib}.orMany({loopContext.loopScope.Value.LoopExitsList});");
                    if (flowContext.branchScope.HasValue)
                    {
                        context.WriteLine(output, $"{flowContext.branchScope.Value.JumpedOutFlag} = true;");
                    }
                    context.WriteLine(output, "}");

                    break;

                case IfStmt ifStmt:
                    /* Prologue */

                    var condTemp = context.FreshTempVar();
                    Debug.Assert(ifStmt.Condition.Type.IsSameTypeAs(PrimitiveType.Bool));
                    context.Write(output, $"{GetSymbolicType(PrimitiveType.Bool)} {condTemp} = ");
                    WriteExpr(context, output, flowContext.pcScope, ifStmt.Condition);
                    context.WriteLine(output, ";");

                    ControlFlowContext thenContext = flowContext.FreshBranchSubContext(context);
                    ControlFlowContext elseContext = flowContext.FreshBranchSubContext(context);

                    context.WriteLine(output, $"Bdd {thenContext.pcScope.PathConstraintVar} = trueCond({condTemp});");
                    context.WriteLine(output, $"Bdd {elseContext.pcScope.PathConstraintVar} = falseCond({condTemp});");

                    context.WriteLine(output, $"boolean {thenContext.branchScope.Value.JumpedOutFlag} = false;");
                    context.WriteLine(output, $"boolean {elseContext.branchScope.Value.JumpedOutFlag} = false;");

                    /* Body */

                    context.WriteLine(output, $"if (!{CompilationContext.BddLib}.isConstFalse({thenContext.pcScope.PathConstraintVar})) {{");
                    context.WriteLine(output, "// 'then' branch");
                    WriteStmt(function, context, output, thenContext, ifStmt.ThenBranch);
                    context.WriteLine(output, "}");
                    
                    if (!(ifStmt.ElseBranch is null))
                    {
                        context.WriteLine(output, $"if (!{CompilationContext.BddLib}.isConstFalse({elseContext.pcScope.PathConstraintVar})) {{");
                        context.WriteLine(output, "// 'else' branch");
                        WriteStmt(function, context, output, elseContext, ifStmt.ElseBranch);
                        context.WriteLine(output, "}");
                    }

                    /* Epilogue */

                    context.WriteLine(output, $"if ({thenContext.branchScope.Value.JumpedOutFlag} || {elseContext.branchScope.Value.JumpedOutFlag}) {{");
                    context.WriteLine(output, $"{flowContext.pcScope.PathConstraintVar} = {CompilationContext.BddLib}.or({thenContext.pcScope.PathConstraintVar}, {elseContext.pcScope.PathConstraintVar});");

                    if (flowContext.branchScope.HasValue)
                    {
                        context.WriteLine(output, $"{flowContext.branchScope.Value.JumpedOutFlag} = true;");
                    }

                    context.WriteLine(output, "}");

                    break;

                case FunCallStmt funCallStmt:
                    var isStatic = funCallStmt.Function.Owner == null;
                    if (!isStatic)
                    {
                        throw new NotImplementedException("Calls to non-static methods not yet supported");
                    }

                    var isAsync = funCallStmt.Function.CanReceive == true;
                    if (isAsync)
                    {
                        throw new NotImplementedException("Calls to async methods not yet supported");
                    }

                    context.Write(output, $"{context.GetNameForDecl(funCallStmt.Function)}({flowContext.pcScope.PathConstraintVar}");
                    foreach (var param in funCallStmt.ArgsList)
                    {
                        context.Write(output, ", ");
                        WriteExpr(context, output, flowContext.pcScope, param);
                    }
                    context.WriteLine(output, ");");

                    break;
 
                default:
                    context.WriteLine(output, $"/* Skipping statement '{stmt.GetType().Name}' */");
                    // throw new NotImplementedException($"Statement type '{stmt.GetType().Name}' is not supported");
                    break;
            }
        }

        private void WriteWithLValueMutationContext(
            CompilationContext context,
            StringWriter output,
            PathConstraintScope pcScope,
            IPExpr lvalue,
            bool needOrigValue,
            Action<string> writeMutator)
        {
            switch (lvalue)
            {
                case MapAccessExpr mapAccessExpr:
                    PLanguageType valueType = mapAccessExpr.Type;
                    PLanguageType indexType = mapAccessExpr.IndexExpr.Type;

                    WriteWithLValueMutationContext(
                        context,
                        output,
                        pcScope,
                        mapAccessExpr.MapExpr,
                        true,
                        mapTemp =>
                        {
                            var valueTemp = context.FreshTempVar();
                            var indexTemp = context.FreshTempVar();

                            context.Write(output, $"{GetSymbolicType(indexType)} {indexTemp} = ");
                            WriteExpr(context, output, pcScope, mapAccessExpr.IndexExpr);
                            context.WriteLine(output, ";");

                            context.Write(output, $"{GetSymbolicType(valueType)} {valueTemp}");
                            if (needOrigValue)
                            {
                                context.WriteLine(output, $" = unwrapOrThrow({GetValueSummaryOps(context, mapAccessExpr.Type).GetName()}.get({mapTemp}, {indexTemp}));");
                            }
                            else
                            {
                                context.WriteLine(output, ";");
                            }

                            writeMutator(valueTemp);

                            context.WriteLine(output, $"{mapTemp} = {GetValueSummaryOps(context, mapAccessExpr.Type).GetName()}.put({mapTemp}, {indexTemp}, {valueTemp})");
                        }
                    );
                    break;
                case NamedTupleAccessExpr namedTupleAccessExpr:
                    throw new NotImplementedException("Named tuples not yet supported");
                case SeqAccessExpr seqAccessExpr:
                    PLanguageType elementType = seqAccessExpr.Type;

                    WriteWithLValueMutationContext(
                        context,
                        output,
                        pcScope,
                        seqAccessExpr.SeqExpr,
                        true,
                        seqTemp =>
                        {
                            var elementTemp = context.FreshTempVar();
                            var indexTemp = context.FreshTempVar();

                            context.Write(output, $"{GetSymbolicType(PrimitiveType.Int)} {indexTemp} = ");
                            WriteExpr(context, output, pcScope, seqAccessExpr.IndexExpr);
                            context.WriteLine(output, ";");

                            context.Write(output, $"{GetSymbolicType(elementType)} {elementTemp}");
                            if (needOrigValue)
                            {
                                context.WriteLine(output, $" = unwrapOrThrow({GetValueSummaryOps(context, seqAccessExpr.Type).GetName()}.get({seqTemp}, {indexTemp}));");
                            }
                            else
                            {
                                context.WriteLine(output, ";");
                            }

                            writeMutator(elementTemp);

                            context.WriteLine(output, $"{seqTemp} = unwrapOrThrow({GetValueSummaryOps(context, seqAccessExpr.Type).GetName()}.set({seqTemp}, {indexTemp}, {elementTemp}));");
                        }
                    );
                    break;
                case TupleAccessExpr tupleAccessExpr:
                    throw new NotImplementedException("Tuples not yet supported");
                case VariableAccessExpr variableAccessExpr:
                    var name = variableAccessExpr.Variable.Name;
                    var type = variableAccessExpr.Variable.Type;

                    var unguarded = CompilationContext.GetVar(name);
                    var summaryOps = GetValueSummaryOps(context, type).GetName();

                    var guardedTemp = context.FreshTempVar();

                    context.WriteLine(output, 
                        $"{GetSymbolicType(variableAccessExpr.Type)} {guardedTemp} = " +
                        $"{summaryOps}.guard({unguarded}, {pcScope.PathConstraintVar})");

                    writeMutator(guardedTemp);

                    context.WriteLine(output,
                        $"{unguarded} = {summaryOps}.merge2(" +
                        $"{summaryOps}.guard({unguarded}, {CompilationContext.BddLib}.not({pcScope.PathConstraintVar}))," +
                        $"{guardedTemp});");

                    break;
                
                default:
                    throw new ArgumentOutOfRangeException($"Expression type '{lvalue.GetType().Name}' is not an lvalue");
            }
        }

        private void WriteExpr(CompilationContext context, StringWriter output, PathConstraintScope pcScope, IPExpr expr)
        {
            switch (expr)
            {
                case CloneExpr cloneExpr:
                    WriteExpr(context, output, pcScope, cloneExpr.Term);
                    break;
                case BinOpExpr binOpExpr:
                    if (binOpExpr.Operation == BinOpType.Eq || binOpExpr.Operation == BinOpType.Neq)
                    {
                        throw new NotImplementedException("'==' and '!=' operations not yet supported");
                    }

                    if (!(binOpExpr.Lhs.Type is PrimitiveType && binOpExpr.Rhs.Type is PrimitiveType))
                    {
                        throw new NotImplementedException("Binary operations are currently only supported between primitive types");
                    }

                    var lhsLambdaTemp = context.FreshTempVar();
                    var rhsLambdaTemp = context.FreshTempVar();

                    context.Write(output, "(");
                    WriteExpr(context, output, pcScope, binOpExpr.Lhs);
                    context.Write(output, ").map2(");
                    WriteExpr(context, output, pcScope, binOpExpr.Rhs);
                    context.Write(
                        output,
                        $", {CompilationContext.BddLib}, " +
                        $"({lhsLambdaTemp}, {rhsLambdaTemp}) => " +
                        $"{lhsLambdaTemp} {BinOpToStr(binOpExpr.Operation)} {rhsLambdaTemp})"
                    );

                    break;
                case BoolLiteralExpr boolLiteralExpr:
                    {
                        var unguarded = $"new { GetSymbolicType(PrimitiveType.Bool) }({ CompilationContext.BddLib}, {boolLiteralExpr.Value})";
                        var guarded = $"{GetValueSummaryOps(context, PrimitiveType.Bool).GetName()}.guard({unguarded}, {pcScope.PathConstraintVar})";
                        context.Write(output, guarded);
                        break;
                    }
                case DefaultExpr defaultExpr:
                    context.Write(output, GetDefaultValue(context, pcScope, defaultExpr.Type));
                    break;
                case FloatLiteralExpr floatLiteralExpr:
                    {
                        var unguarded = $"new { GetSymbolicType(PrimitiveType.Float) }({ CompilationContext.BddLib}, {floatLiteralExpr.Value})";
                        var guarded = $"{GetValueSummaryOps(context, PrimitiveType.Float).GetName()}.guard({unguarded}, {pcScope.PathConstraintVar})";
                        context.Write(output, guarded);
                        break;
                    }
                case IntLiteralExpr intLiteralExpr:
                    {
                        var unguarded = $"new { GetSymbolicType(PrimitiveType.Int) }({ CompilationContext.BddLib}, {intLiteralExpr.Value})";
                        var guarded = $"{GetValueSummaryOps(context, PrimitiveType.Int).GetName()}.guard({unguarded}, {pcScope.PathConstraintVar})";
                        context.Write(output, guarded);
                        break;
                    }
                case MapAccessExpr mapAccessExpr:
                    context.Write(output, $"unwrapOrThrow({GetValueSummaryOps(context, mapAccessExpr.Type).GetName()}.get(");
                    WriteExpr(context, output, pcScope, mapAccessExpr.MapExpr);
                    context.Write(output, ", ");
                    WriteExpr(context, output, pcScope, mapAccessExpr.IndexExpr);
                    context.Write(output, "))");
                    break;
                case SeqAccessExpr seqAccessExpr:
                    context.Write(output, $"unwrapOrThrow({GetValueSummaryOps(context, seqAccessExpr.Type).GetName()}.get(");
                    WriteExpr(context, output, pcScope, seqAccessExpr.SeqExpr);
                    context.Write(output, ", ");
                    WriteExpr(context, output, pcScope, seqAccessExpr.IndexExpr);
                    context.Write(output, "))");
                    break;
                case VariableAccessExpr variableAccessExpr:
                    context.Write(output,
                        $"{GetValueSummaryOps(context, variableAccessExpr.Type).GetName()}.guard(" +
                        $"{CompilationContext.GetVar(variableAccessExpr.Variable.Name)}, " +
                        $"{pcScope.PathConstraintVar})");
                    break;
                case LinearAccessRefExpr linearAccessExpr:
                    context.Write(output,
                        $"{GetValueSummaryOps(context, linearAccessExpr.Type).GetName()}.guard(" +
                        $"{CompilationContext.GetVar(linearAccessExpr.Variable.Name)}, " +
                        $"{pcScope.PathConstraintVar})");
                    break;
                default:
                    context.Write(output, $"/* Skipping expr '{expr.GetType().Name}' */");
                    break;
            }
        }

        // TODO: This is copied from PSharpCodeGenerator.cs.  Should we factor this out into some common location?
        private object BinOpToStr(BinOpType binOpType)
        {
            switch (binOpType)
            {
                case BinOpType.Add:
                    return "+";
                case BinOpType.Sub:
                    return "-";
                case BinOpType.Mul:
                    return "*";
                case BinOpType.Div:
                    return "/";
                case BinOpType.Lt:
                    return "<";
                case BinOpType.Le:
                    return "<=";
                case BinOpType.Gt:
                    return ">";
                case BinOpType.Ge:
                    return ">=";
                case BinOpType.And:
                    return "&&";
                case BinOpType.Or:
                    return "||";
                default:
                    throw new ArgumentOutOfRangeException(nameof(binOpType), binOpType, null);
            }
        }

        private string GetConcreteBoxedType(PLanguageType type)
        {
            switch (type)
            {
                case PrimitiveType primitiveType when primitiveType.IsSameTypeAs(PrimitiveType.Bool):
                    return "Boolean";
                case PrimitiveType primitiveType when primitiveType.IsSameTypeAs(PrimitiveType.Int):
                    return "Integer";
                case PrimitiveType primitiveType when primitiveType.IsSameTypeAs(PrimitiveType.Float):
                    return "Float";
                default:
                    throw new NotImplementedException($"Concrete type '{type.OriginalRepresentation}' is not supported");
            }
        }

        private string GetSymbolicType(PLanguageType type, bool isVar = false)
        {
            switch (type.Canonicalize())
            {
                case PrimitiveType primitiveType when primitiveType.IsSameTypeAs(PrimitiveType.Bool):
                    return "PrimVS<Bdd, Boolean>";
                case PrimitiveType primitiveType when primitiveType.IsSameTypeAs(PrimitiveType.Int):
                    return "PrimVS<Bdd, Integer>";
                case PrimitiveType primitiveType when primitiveType.IsSameTypeAs(PrimitiveType.Float):
                    return "PrimVS<Bdd, Float>";
                case PrimitiveType primitiveType when primitiveType.IsSameTypeAs(PrimitiveType.Null):
                    if (isVar)
                        throw new NotImplementedException("Variables of type 'null' not yet supported");
                    else
                        return "void";
                case SequenceType sequenceType:
                    return $"ListVS<Bdd, {GetSymbolicType(sequenceType.ElementType, true)}>";
                case MapType mapType:
                    return $"MapVS<" +
                        $"Bdd, " +
                        $"{GetConcreteBoxedType(mapType.KeyType)}, " +
                        $"{GetSymbolicType(mapType.ValueType, true)}>";
                default:
                    throw new NotImplementedException($"Symbolic type '{type.OriginalRepresentation}' not supported");
            }

            throw new NotImplementedException();
        }

        private string GetValueSummaryOpsType(PLanguageType type)
        {
            switch (type)
            {
                case PrimitiveType primitiveType when primitiveType.IsSameTypeAs(PrimitiveType.Bool):
                    return "PrimVS.Ops<Bdd, Boolean>";
                case PrimitiveType primitiveType when primitiveType.IsSameTypeAs(PrimitiveType.Int):
                    return "PrimVS.Ops<Bdd, Integer>";
                case PrimitiveType primitiveType when primitiveType.IsSameTypeAs(PrimitiveType.Float):
                    return "PrimVS.Ops<Bdd, Float>";
                case SequenceType sequenceType:
                    return $"ListVS.Ops<Bdd, {GetSymbolicType(sequenceType.ElementType, true)}>";
                case MapType mapType:
                    return $"MapVS.Ops<" +
                        $"Bdd, " +
                        $"{GetConcreteBoxedType(mapType.KeyType)}, " +
                        $"{GetSymbolicType(mapType.ValueType, true)}>";
                default:
                    throw new NotImplementedException($"Symbolic type '{type.OriginalRepresentation}' ops type not supported");
            }
        }

        private ValueSummaryOps GetValueSummaryOps(CompilationContext context, PLanguageType type)
        {
            var opsType = GetValueSummaryOpsType(type);
            string defBody;
            switch (type)
            {
                case PrimitiveType primitiveType when
                    primitiveType.IsSameTypeAs(PrimitiveType.Bool) ||
                    primitiveType.IsSameTypeAs(PrimitiveType.Int) ||
                    primitiveType.IsSameTypeAs(PrimitiveType.Float):

                    defBody = $"new {opsType}({CompilationContext.BddLib})";
                    break;

                case SequenceType sequenceType:
                    var elemOps = GetValueSummaryOps(context, sequenceType.ElementType);
                    defBody = $"new {opsType}({CompilationContext.BddLib}, {elemOps.GetName()})";
                    break;
                case MapType mapType:
                    var valOps = GetValueSummaryOps(context, mapType.ValueType);
                    defBody = $"new {opsType}({CompilationContext.BddLib}, {valOps.GetName()})";
                    break;
                default:
                    throw new NotImplementedException($"Symbolic type '{type.OriginalRepresentation}' ops not supported");
            }

            return context.ValueSummaryOpsForDef(new ValueSummaryOpsDef(opsType, defBody));
        }

        private string GetDefaultValue(CompilationContext context, PathConstraintScope pcScope, PLanguageType type)
        {
            string unguarded;
            switch (type.Canonicalize())
            {
                case PrimitiveType primitiveType when primitiveType.IsSameTypeAs(PrimitiveType.Bool):
                    unguarded = $"new {GetSymbolicType(type)}({CompilationContext.BddLib}, false)";
                    break;
                case PrimitiveType primitiveType when primitiveType.IsSameTypeAs(PrimitiveType.Int):
                    unguarded = $"new {GetSymbolicType(type)}({CompilationContext.BddLib}, 0)";
                    break;
                case PrimitiveType primitiveType when primitiveType.IsSameTypeAs(PrimitiveType.Float):
                    unguarded = $"new {GetSymbolicType(type)}({CompilationContext.BddLib}, 0.0f)";
                    break;
                case SequenceType sequenceType:
                    unguarded = $"new {GetSymbolicType(type)}({CompilationContext.BddLib})";
                    break;
                case MapType mapType:
                    unguarded = $"new {GetSymbolicType(type)}({CompilationContext.BddLib})";
                    break;
                default:
                    throw new NotImplementedException($"Default value for symbolic type '{type.OriginalRepresentation}' not supported");
            }

            var guarded = $"{GetValueSummaryOps(context, type).GetName()}.guard({unguarded}, {pcScope.PathConstraintVar})";
            return guarded;
        }

        private void WriteSourcePrologue(CompilationContext context, StringWriter output)
        {
            context.WriteLine(output, "/* TODO: Import appropriate symbols from runtime library */");
            context.WriteLine(output);
            context.WriteLine(output, $"public class {context.MainClassName} {{");
        }

        private void WriteSourceEpilogue(CompilationContext context, StringWriter output)
        {
            for (int i = 0; i < context.PendingValueSummaryOpsDefs.Count; i++)
            {
                var def = context.PendingValueSummaryOpsDefs[i];
                var name = new ValueSummaryOps(i).GetName();
                context.WriteLine(output, $"private static final {def.opsType} {name} =");
                context.WriteLine(output, $"    {def.opsDef};");
                context.WriteLine(output);
            }

            context.WriteLine(output, "}");
        }
    }
}