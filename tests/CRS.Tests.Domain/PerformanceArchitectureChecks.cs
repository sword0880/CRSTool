using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>使用 SDK 自带语法分析器建立增量门禁；复杂度是分支语法计数，不冒充完整控制流分析。</summary>
internal static class PerformanceArchitectureChecks
{
    private sealed record Budget(int Branches,int Lines);
    internal static void Run(string root,bool capture=false)
    {
        var budgetsPath=Path.Combine(root,"tests/fixtures/performance-complexity.json");
        var budgets=File.Exists(budgetsPath)?JsonSerializer.Deserialize<Dictionary<string,Budget>>(File.ReadAllText(budgetsPath))!:[];
        var reviewed=new Dictionary<string,Budget>();var failures=new List<string>();
        foreach(var folder in new[]{"CRS.Domain/Performance","CRS.Application/Performance","CRS.Infrastructure/Performance","CRS.Desktop.Wpf/ViewModels"})
        foreach(var file in Directory.EnumerateFiles(Path.Combine(root,"src",folder),"*.cs",SearchOption.AllDirectories))
        {
            if(folder.EndsWith("ViewModels",StringComparison.Ordinal) && Path.GetFileName(file)!="PerformanceViewModel.cs")continue;
            var tree=CSharpSyntaxTree.ParseText(File.ReadAllText(file));
            foreach(var method in tree.GetRoot().DescendantNodes().OfType<BaseMethodDeclarationSyntax>().Where(m=>m is MethodDeclarationSyntax or ConstructorDeclarationSyntax))
            {
                var owner=string.Join(".",method.Ancestors().OfType<TypeDeclarationSyntax>().Reverse().Select(t=>t.Identifier.Text));
                var name=method is MethodDeclarationSyntax declaration?declaration.Identifier.Text:".ctor";
                var key=owner+"."+name+"("+string.Join(",",method.ParameterList.Parameters.Select(p=>p.Type?.ToString()))+")";
                var nodes=method.DescendantNodes().ToArray();
                var branches=1+nodes.Count(n=>n is IfStatementSyntax or ForStatementSyntax or ForEachStatementSyntax or WhileStatementSyntax or DoStatementSyntax or CatchClauseSyntax or ConditionalExpressionSyntax or SwitchExpressionArmSyntax or CaseSwitchLabelSyntax or CasePatternSwitchLabelSyntax
                    || n is BinaryExpressionSyntax b && b.Kind() is SyntaxKind.LogicalAndExpression or SyntaxKind.LogicalOrExpression or SyntaxKind.CoalesceExpression
                    || n is BinaryPatternSyntax p && p.Kind() is SyntaxKind.AndPattern or SyntaxKind.OrPattern);
                var lines=method.DescendantTokens().Where(t=>!t.IsKind(SyntaxKind.OpenBraceToken) && !t.IsKind(SyntaxKind.CloseBraceToken))
                    .Select(t=>tree.GetLineSpan(t.Span).StartLinePosition.Line).Distinct().Count();
                if(branches<=15 && lines<=50)continue;
                reviewed[key]=new(branches,lines);
                if(!capture && (!budgets.TryGetValue(key,out var budget) || branches>Math.Max(15,budget.Branches) || lines>Math.Max(50,budget.Lines)))
                    failures.Add($"{key}: branches={branches}, lines={lines}");
            }
        }
        if(capture){File.WriteAllText(budgetsPath,JsonSerializer.Serialize(reviewed,new JsonSerializerOptions{WriteIndented=true}));Console.WriteLine("已记录绩效复杂度评审基线；不得未经评审自动抬高。");return;}
        if(failures.Count>0)throw new Exception("绩效方法超过已评审规模/分支基线："+string.Join("; ",failures));
        Console.WriteLine($"绩效静态门禁通过；{reviewed.Count} 个既有高分支/长方法保留评审基线，新方法阈值 15/50。");
    }
}
