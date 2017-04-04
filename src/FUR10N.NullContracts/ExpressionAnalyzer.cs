﻿using FUR10N.NullContracts.FlowAnalysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using System;
using System.Collections.Generic;
using System.Diagnostics.Contracts;

namespace FUR10N.NullContracts
{
    internal class ExpressionAnalyzer : CSharpSyntaxWalker
    {
        private readonly CodeBlockAnalysisContext context;

        private readonly Dictionary<ISymbol, MethodFlowAnalysis> needsConstraintChecking = new Dictionary<ISymbol, MethodFlowAnalysis>();

        public ExpressionAnalyzer(CodeBlockAnalysisContext context)
        {
            this.context = context;
        }

        public void Analyze(SyntaxNode node)
        {
            needsConstraintChecking.Clear();
            this.Visit(node);

            foreach (var method in needsConstraintChecking.Values)
            {
                foreach (var violation in method.GetAssignmentsAfterConstraints())
                {
                    context.ReportDiagnostic(
                        MainAnalyzer.CreateAssignmentAfterConstraint(violation.Expression.GetLocation(), violation.Expression.ToString()));
                }
            }
        }

        public override void VisitBinaryExpression(BinaryExpressionSyntax node)
        {
            base.VisitBinaryExpression(node);

            if (node.IsCheckAgainstNull(out var target, out var valueType))
            {
                ReportIfIsNotNullSymbol(target);
                return;
            }
            if (node.Kind() == SyntaxKind.CoalesceExpression)
            {
                ReportIfIsNotNullSymbol(node.Left);
            }
        }

        public override void VisitConditionalAccessExpression(ConditionalAccessExpressionSyntax node)
        {
            base.VisitConditionalAccessExpression(node);

            ReportIfIsNotNullSymbol(node.Expression);
        }

        public override void VisitInvocationExpression(InvocationExpressionSyntax node)
        {
            base.VisitInvocationExpression(node);

            var methodDefinition = context.SemanticModel.GetSymbolInfo(node).Symbol as IMethodSymbol;
            if (methodDefinition == null)
            {
                return;
            }
            if (methodDefinition.IsConstraintMethod())
            {
                if (node.IsConstraint(context.SemanticModel, out var expression))
                {
                    var symbol = context.SemanticModel.GetSymbolInfo(expression).Symbol;
                    if (symbol.HasNotNullOrCheckNull())
                    {
                        context.ReportDiagnostic(MainAnalyzer.CreateUnneededConstraint(node.GetLocation(), symbol.ToString()));
                    }
                }
                else
                {
                    context.ReportDiagnostic(MainAnalyzer.CreateInvalidConstraintError(node.GetLocation()));
                }
            }
            CheckMethodInvocation(
                node,
                methodDefinition,
                node.ArgumentList,
                (status, location, error) => ReportIssue(status, location, error));
        }

        public override void VisitObjectCreationExpression(ObjectCreationExpressionSyntax node)
        {
            base.VisitObjectCreationExpression(node);

            var ctor = context.SemanticModel.GetSymbolInfo(node).Symbol as IMethodSymbol;
            if (ctor == null)
            {
                return;
            }
            CheckMethodInvocation(
                node,
                ctor,
                node.ArgumentList,
                (status, location, error) => ReportIssue(status, location, error));
        }

        public override void VisitConstructorInitializer(ConstructorInitializerSyntax node)
        {
            base.VisitConstructorInitializer(node);

            var ctor = context.SemanticModel.GetSymbolInfo(node).Symbol as IMethodSymbol;
            if (ctor == null)
            {
                return;
            }
            CheckMethodInvocation(
                node,
                ctor,
                node.ArgumentList,
                (status, l, s) =>
                {
                    if (status == ExpressionStatus.NotAssigned)
                        context.ReportDiagnostic(MainAnalyzer.CreatePropagateNotNullInCtors(l, s));
                });
        }

        public override void VisitAssignmentExpression(AssignmentExpressionSyntax node)
        {
            base.VisitAssignmentExpression(node);

            var valueType = node.Right.GetTypeOfValue(context.SemanticModel);
            if (valueType == ValueType.NotNull)
            {
                return;
            }
            var target = node.Left.FindUnderlyingMember();
            if (target == null)
            {
                return;
            }
            
            var symbol = context.SemanticModel.GetSymbolInfo(target).Symbol;
            if (!symbol.HasNotNullOrCheckNull())
            {
                return;
            }

            switch (GetAssignmentStatus(node.Right, node, valueType))
            {
                case ExpressionStatus.NotAssigned:
                    context.ReportDiagnostic(MainAnalyzer.CreateNullAssignmentError(node.GetLocation(), symbol));
                    break;
                case ExpressionStatus.ReassignedAfterCondition:
                    context.ReportDiagnostic(MainAnalyzer.CreateAssignmentAfterCondition(node.GetLocation(), node.ToString()));
                    break;
                case ExpressionStatus.AssignedWithUnneededConstraint:
                    context.ReportDiagnostic(MainAnalyzer.CreateUnneededConstraint(node.GetLocation(), node.ToString()));
                    break;
            }
        }

        private void ReportIssue(ExpressionStatus status, Location location, string errorContext)
        {
            switch (status)
            {
                case ExpressionStatus.NotAssigned:
                    context.ReportDiagnostic(MainAnalyzer.CreateNullAssignmentError(location, errorContext));
                    break;
                case ExpressionStatus.ReassignedAfterCondition:
                    context.ReportDiagnostic(MainAnalyzer.CreateAssignmentAfterCondition(location, errorContext));
                    break;
                case ExpressionStatus.AssignedWithUnneededConstraint:
                    context.ReportDiagnostic(MainAnalyzer.CreateUnneededConstraint(location, errorContext));
                    break;
            }
        }

        // This is kinda duplicated in ClassAnalyzer.CheckExpressionForNull
        private ExpressionStatus GetAssignmentStatus(ExpressionSyntax expression, SyntaxNode parent, ValueType valueOfExpression)
        {
            if (valueOfExpression == ValueType.NotNull)
            {
                // Argument cannot be null, so move to the next
                return ExpressionStatus.Assigned;
            }
            if (valueOfExpression == ValueType.Null)
            {
                return ExpressionStatus.NotAssigned;
            }

            var methodInfo = expression.GetParentMethod(context.SemanticModel);
            if (methodInfo.Item1 != null)
            {
                var analysis = Cache.Get(context.SemanticModel).GetMethodAnalysis(methodInfo.Item1, methodInfo.Item2, methodInfo.Item3);
                if (analysis.HasConstraints)
                {
                    needsConstraintChecking[methodInfo.Item1] = analysis;
                }
                return analysis.IsAlwaysAssigned(expression, parent);
            }
            return ExpressionStatus.NotAssigned;
        }

        private void CheckMethodInvocation(
            SyntaxNode node,
            IMethodSymbol methodDefinition,
            ArgumentListSyntax argumentList,
            Action<ExpressionStatus, Location, string> reportAction)
        {
            try
            {
                if (argumentList == null)
                {
                    // Is object initializer
                    return;
                }
                var parameters = methodDefinition.Parameters.GetEnumerator();
                foreach (var arg in argumentList.Arguments)
                {
                    parameters.MoveNext();
                    if (parameters.Current.IsParams)
                    {
                        // Ignore for 'params' parameter
                        return;
                    }
                    if (parameters.Current.RefKind == RefKind.Ref)
                    {
                        var argSymbol = context.SemanticModel.GetSymbolInfo(arg.Expression).Symbol;
                        if (argSymbol.HasNotNullOrCheckNull())
                        {
                            context.ReportDiagnostic(MainAnalyzer.CreateNotNullAsRefParameter(arg.Expression.GetLocation(), arg.Expression.ToString()));
                            continue;
                        }
                    }
                    if (!parameters.Current.HasNotNullOrCheckNull())
                    {
                        // Only check [NotNull] parameters
                        continue;
                    }
                    var status = GetAssignmentStatus(arg.Expression, node, arg.Expression.GetTypeOfValue(context.SemanticModel));
                    reportAction(status, arg.GetLocation(), $"{methodDefinition.Name}({arg.Expression})");
                }
            }
            catch (Exception ex)
            {
                throw new ParseFailedException(node.GetLocation(), $"{ex.Message} --> Parse failed on: {methodDefinition.GetFullName()}", ex);
            }
        }

        [Pure]
        private void ReportIfIsNotNullSymbol(ExpressionSyntax expression)
        {
            var target = expression.FindUnderlyingMember();
            if (target == null)
            {
                return;
            }
            if (target is ConditionalExpressionSyntax conditional)
            {
                ReportIfIsNotNullSymbol(conditional.WhenTrue);
                ReportIfIsNotNullSymbol(conditional.WhenFalse);
                return;
            }

            // Make sure the expression is always Not Null. This catches the case: ([NotNull]x as SomeType) which can be null.
            if (expression.GetTypeOfValue(context.SemanticModel) == ValueType.NotNull)
            {
                var symbol = context.SemanticModel.GetSymbolInfo(target).Symbol;
                if (symbol.HasNotNullOrCheckNull())
                {
                    context.ReportDiagnostic(MainAnalyzer.CreateUnneededNullCheckError(expression.GetLocation(), symbol));
                }
            }
        }
    }
}
