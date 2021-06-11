﻿using HECSFramework.Core;
using HECSFramework.Core.Generator;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.MSBuild;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ClassDeclarationSyntax = Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax;
using SyntaxNode = Microsoft.CodeAnalysis.SyntaxNode;

namespace RoslynHECS
{
    class Program
    {
        public static List<string> components = new List<string>(256);
        public static List<ClassDeclarationSyntax> componentsDeclarations = new List<ClassDeclarationSyntax>(256);
        public static List<ClassDeclarationSyntax> systemsDeclarations = new List<ClassDeclarationSyntax>(256);
        public static List<StructDeclarationSyntax> globalCommands = new List<StructDeclarationSyntax>(256);
        public static List<StructDeclarationSyntax> localCommands = new List<StructDeclarationSyntax>(256);

        public const string AssetPath = @"D:\Develop\CyberMafia\Assets\";
        public const string HECSGenerated = @"\Scripts\HECSGenerated\";
        public const string SolutionPath = @"D:\Develop\CyberMafia\CyberMafia.sln";


        private const string TypeProvider = "TypeProvider.cs";
        private const string MaskProvider = "MaskProvider.cs";
        private const string HecsMasks = "HECSMasks.cs";
        private const string SystemBindings = "SystemBindings.cs";
        private const string ComponentContext = "ComponentContext.cs";
        private const string BluePrintsProvider = "BluePrintsProvider.cs";
        private const string Documentation = "Documentation.cs";
        private const string MapResolver = "MapResolver.cs";

        static async Task Main(string[] args)
        {
            // Attempt to set the version of MSBuild.
            var visualStudioInstances = MSBuildLocator.QueryVisualStudioInstances().ToArray();
            var instance = visualStudioInstances.Length == 1
                // If there is only one instance of MSBuild on this machine, set that as the one to use.
                ? visualStudioInstances[0]
                // Handle selecting the version of MSBuild you want to use.
                : SelectVisualStudioInstance(visualStudioInstances);

            Console.WriteLine($"Using MSBuild at '{instance.MSBuildPath}' to load projects.");

            // NOTE: Be sure to register an instance with the MSBuildLocator 
            //       before calling MSBuildWorkspace.Create()
            //       otherwise, MSBuildWorkspace won't MEF compose.
            MSBuildLocator.RegisterInstance(instance);

            using (var workspace = MSBuildWorkspace.Create())
            {
                // Print message for WorkspaceFailed event to help diagnosing project load failures.
                workspace.WorkspaceFailed += (o, e) => Console.WriteLine(e.Diagnostic.Message);

                
                Console.WriteLine($"Loading solution '{SolutionPath}'");

                // Attach progress reporter so we print projects as they are loaded.
                var solution = await workspace.OpenSolutionAsync(SolutionPath, new ConsoleProgressReporter());
                Console.WriteLine($"Finished loading solution '{SolutionPath}'");

                var compilations = await Task.WhenAll(solution.Projects.Select(x => x.GetCompilationAsync()));

                // TODO: Do analysis on the projects in the loaded solution

                var classVisitor = new ClassVirtualizationVisitor();
                var structVisitor = new StructVirtualizationVisitor();
                
                List<INamedTypeSymbol> types = new List<INamedTypeSymbol>(256);

                foreach (var compilation in compilations)
                {
                    var list = GetTypesByMetadataName(compilation, "BaseComponent").ToList();
                    types.AddRange(list);

                    foreach (var syntaxTree in compilation.SyntaxTrees)
                    {
                        classVisitor.Visit(syntaxTree.GetRoot());
                        structVisitor.Visit(syntaxTree.GetRoot());
                    }
                }

                var classes = classVisitor.Classes;
                var structs = structVisitor.Structs;

                foreach (var c in classes)
                    ProcessClasses(c);

                foreach (var s in structs)
                    ProcessStructs(s);

                SaveFiles();

                Console.WriteLine("нашли компоненты " + components.Count);
            }
        }

        private static void SaveFiles()
        {
            var processGeneration = new CodeGenerator();
            SaveToFile(SystemBindings, processGeneration.GetSystemBindsByRoslyn());
        }

        private static void SaveToFile(string name, string data, string pathToDirectory = AssetPath+HECSGenerated, bool needToImport = false)
        {
            var path = pathToDirectory + name;

            try
            {
                if (!Directory.Exists(pathToDirectory))
                    Directory.CreateDirectory(pathToDirectory);

                File.WriteAllText(path, data);
            }
            catch
            {
                Console.WriteLine("не смогли ослить " + pathToDirectory);
            }
        }

        private static void ProcessStructs(StructDeclarationSyntax s)
        {
            var structCurrent = s.Identifier.ValueText;

            if (s.BaseList != null && s.BaseList.ChildNodes().Any(x => x.ToString().Contains(typeof(IGlobalCommand).Name)))
            {
                globalCommands.Add(s);
                Console.WriteLine("нашли глобальную команду " + structCurrent);
            } 
            
            if (s.BaseList != null && s.BaseList.ChildNodes().Any(x => x.ToString().Contains(typeof(ICommand).Name)))
            {
                localCommands.Add(s);
                Console.WriteLine("нашли локальную команду " + structCurrent);
            }
        }

        public static IEnumerable<INamedTypeSymbol> GetTypesByMetadataName(Compilation compilation, string typeMetadataName)
        {
            return compilation.References
                .Select(compilation.GetAssemblyOrModuleSymbol)
                .OfType<IAssemblySymbol>()
                .Select(assemblySymbol => assemblySymbol.GetTypeByMetadataName(typeMetadataName))
                .Where(t => t != null);
        }

        private static void ProcessClasses(ClassDeclarationSyntax c)
        {
            var classCurrent = c.Identifier.ValueText;
            var baseClass = c.BaseList != null ? c.BaseList.ToString() : string.Empty;
            var isAbstract = c.Modifiers.Any(x => x.IsKind(SyntaxKind.AbstractKeyword));

            if (baseClass.Contains("BaseComponent") && !isAbstract)
            {
                components.Add(classCurrent);
                componentsDeclarations.Add(c);
                Console.WriteLine("нашли компонент " + classCurrent);
            }
            
            if (baseClass.Contains(typeof(BaseSystem).Name) && !isAbstract)
            {
                components.Add(classCurrent);
                systemsDeclarations.Add(c);
                Console.WriteLine("----");
                Console.WriteLine("нашли систему " + classCurrent);
            }
        }

        class ClassVirtualizationVisitor : CSharpSyntaxRewriter
        {
            public ClassVirtualizationVisitor()
            {
                Classes = new List<ClassDeclarationSyntax>();
            }

            public List<ClassDeclarationSyntax> Classes { get; set; }

            public override SyntaxNode VisitClassDeclaration(ClassDeclarationSyntax node)
            {
                node = (ClassDeclarationSyntax)base.VisitClassDeclaration(node);
                Classes.Add(node); // save your visited classes
                return node;
            }
        }        
        
        class StructVirtualizationVisitor : CSharpSyntaxRewriter
        {
            public StructVirtualizationVisitor()
            {
                Structs = new List<StructDeclarationSyntax>();
            }

            public List<StructDeclarationSyntax> Structs { get; set; }

            public override SyntaxNode VisitStructDeclaration(StructDeclarationSyntax node)
            {
                node = (StructDeclarationSyntax)base.VisitStructDeclaration(node);
                Structs.Add(node); // save your visited classes
                return node;
            }
        }

        private static VisualStudioInstance SelectVisualStudioInstance(VisualStudioInstance[] visualStudioInstances)
        {
            Console.WriteLine("Multiple installs of MSBuild detected please select one:");
            for (int i = 0; i < visualStudioInstances.Length; i++)
            {
                Console.WriteLine($"Instance {i + 1}");
                Console.WriteLine($"    Name: {visualStudioInstances[i].Name}");
                Console.WriteLine($"    Version: {visualStudioInstances[i].Version}");
                Console.WriteLine($"    MSBuild Path: {visualStudioInstances[i].MSBuildPath}");
            }

            while (true)
            {
                var userResponse = Console.ReadLine();
                if (int.TryParse(userResponse, out int instanceNumber) &&
                    instanceNumber > 0 &&
                    instanceNumber <= visualStudioInstances.Length)
                {
                    return visualStudioInstances[instanceNumber - 1];
                }
                Console.WriteLine("Input not accepted, try again.");
            }
        }

        private class ConsoleProgressReporter : IProgress<ProjectLoadProgress>
        {
            public void Report(ProjectLoadProgress loadProgress)
            {
                var projectDisplay = Path.GetFileName(loadProgress.FilePath);
                if (loadProgress.TargetFramework != null)
                {
                    projectDisplay += $" ({loadProgress.TargetFramework})";
                }

                Console.WriteLine($"{loadProgress.Operation,-15} {loadProgress.ElapsedTime,-15:m\\:ss\\.fffffff} {projectDisplay}");
            }
        }
    }
}
