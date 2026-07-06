using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Reflection;
using System.Text;
using System.Xml.Linq;

class SolutionDependencyGenerator
{
    private const string REVERSE_FILE_POSTFIX = "_[%reverse%]";

    private const string IMPORT_START = "<!-- SolutionDependencyGenerator Import Start -->";
    private const string IMPORT_END = "<!-- SolutionDependencyGenerator Import End -->";

    /// <summary>
    /// args[0] solution path
    /// </summary>
    /// <param name="args"></param>
    public static void Main(string[] args)
    {
        // show version
        var version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>().InformationalVersion;
        Console.WriteLine($"SolutionDependencyGenerator version: {version}");

        string solutionPath;
        if (args.Length > 0)
        {
            var path = args[0].Trim();
            if (Path.GetExtension(path) == ".csproj")
            {
                solutionPath = path;
            }
            else
            {
                var t = GetCsprojPath(path);
                if (string.IsNullOrEmpty(t))
                {
                    Console.WriteLine($"no solution files (*.csproj). Path: '{path}'");
                    return;
                }
                else
                {
                    solutionPath = t;
                }
            }
        }
        else
        {
            var currentPath = Assembly.GetExecutingAssembly().Location;
            if (string.IsNullOrEmpty(currentPath))
            {
                currentPath = Environment.ProcessPath;
            }

            // DBG
            //Console.WriteLine(currentPath);

            var currentFolder = Path.GetDirectoryName(currentPath);
            var t = GetCsprojPath(currentFolder);
            if (string.IsNullOrEmpty(t))
            {
                Console.WriteLine($"no solution files (*.csproj). Path: '{currentFolder}'");
                return;
            }
            else
            {
                solutionPath = t;
            }
        }

        Console.WriteLine($"csproj path: '{solutionPath}'");

        // get include files
        var so = XDocument.Load(solutionPath);
        var ns = so.Root?.Name.Namespace;
        var compileTagName = (ns == null) ? "Compile" : ns + "Compile";
        var referenceTagName = (ns == null) ? "Reference" : ns + "Reference";
        var hintPathTagName = (ns == null) ? "HintPath" : ns + "HintPath";

        var targetFrameworkVersion = (ns == null) ? "TargetFrameworkVersion" : ns + "TargetFrameworkVersion";

        var fwVersion = so.Descendants(targetFrameworkVersion).First().Value;
        if (fwVersion != "v4.8") 
        {
            Console.WriteLine($"sorry, not supported. version: {fwVersion}");
            return;
        }

        var includedFiles = so.Descendants(compileTagName).Select(t => t.Attribute("Include")?.Value).Where(v => (v != null)).ToList();

        //var includedReferences = so.Descendants(searchReference).Select(t => t.Attribute("Include")?.Value).Where(v => (v != null)).ToList();
        var includedReferences = new List<string>();
        var includes = so.Descendants(referenceTagName);
        foreach(var inc in includes)
        {
            var hintPath = inc.Descendants(hintPathTagName).FirstOrDefault();
            if (hintPath != null)
            {
                includedReferences.Add(hintPath.Value);
            }
            else
            {
                var attr = inc.Attribute("Include");
                if (attr != null)
                {
                    includedReferences.Add(attr.Value);
                }
            }
        }

        Console.WriteLine($"find {includedFiles.Count} files.");
        Console.WriteLine($"find {includedReferences.Count} references.");

        //// DBG listing
        //foreach (var item in includedFiles)
        //{
        //    Console.WriteLine(item);
        //}

        var solutionFolder = Path.GetDirectoryName(solutionPath);

        string stdDLLPath;
        switch (fwVersion)
        {
            case "v4.8":
                stdDLLPath = @"C:\Windows\Microsoft.NET\Framework64\v4.0.30319";
                break;
            default:
                stdDLLPath = string.Empty;
                break;
        }

        // add references
        var metaReferences = new List<MetadataReference>();
        foreach (var r in includedReferences)
        {
            string dllPath;
            if (r.EndsWith(".dll"))
            {
                dllPath = Path.Combine(solutionFolder, r);
            }
            else
            {
                dllPath = Path.Combine(stdDLLPath, $"{r}.dll");

                // WPF?
                if (!File.Exists(dllPath))
                {
                    dllPath = Path.Combine(stdDLLPath, "WPF", $"{r}.dll");
                }
            }

            if (File.Exists(dllPath))
            {
                var metaR = MetadataReference.CreateFromFile(dllPath);
                metaReferences.Add(metaR);
            }
            else
            {
                Console.WriteLine($"Reference DLL does not exists. path: '{dllPath}'");
            }
        }

        // add cs codes
        var syntaxTrees = new List<SyntaxTree>();
        foreach (var fn in includedFiles)
        {
            var path = Path.Combine(solutionFolder, fn);
            if (File.Exists(path))
            {
                var code = File.ReadAllText(path);
                var tree = CSharpSyntaxTree.ParseText(code, path: path);
                syntaxTrees.Add(tree);
            }
            else
            {
                Console.WriteLine($"file does not exists. path: '{path}'");
            }
        }

        // SemanticSymbol
        var compilation = CSharpCompilation.Create("Analysis")
                            .AddReferences(metaReferences)
                            .AddSyntaxTrees(syntaxTrees);

        var dependList = new List<(string from, string to)>();

        var symbolList = new Dictionary<string, INamedTypeSymbol>();

        void AddSymbolList(Dictionary<string, INamedTypeSymbol> list, INamedTypeSymbol sym)
        {
            var fullName = sym.ToDisplayString();
            list.TryAdd(fullName, sym);
        }

        // listing dependencies
        foreach (var tree in syntaxTrees)
        {
            var model = compilation.GetSemanticModel(tree);
            var root = tree.GetRoot();

            Console.WriteLine($"now dependency check. path: '{tree.FilePath}'");

            // check classes
            var classes = root.DescendantNodes().OfType<ClassDeclarationSyntax>();
            foreach (var cls in classes)
            {
                var classSymbol = model.GetDeclaredSymbol(cls);

                var fullName = classSymbol.ToDisplayString();
                AddSymbolList(symbolList, classSymbol);

                var baseClass = classSymbol.BaseType;

                if ((baseClass != null) && (baseClass.Name != "Object"))
                {
                    var baseFullName = baseClass.ToDisplayString();
                    dependList.Add((fullName, baseFullName));

                    AddSymbolList(symbolList, baseClass);
                }

                var ifs = classSymbol.Interfaces;
                foreach (var i in ifs)
                {
                    var ifFullName = i.ToDisplayString();
                    dependList.Add((fullName, ifFullName));

                    AddSymbolList(symbolList, i);
                }
            }

            // check interfaces
            var interfaces = root.DescendantNodes().OfType<InterfaceDeclarationSyntax>();
            foreach (var i in interfaces)
            {
                var ifSymbol = model.GetDeclaredSymbol(i);
                var fullName = ifSymbol.ToDisplayString();
                AddSymbolList(symbolList, ifSymbol);

                var pIfs = ifSymbol.Interfaces;
                foreach (var p in pIfs)
                {
                    var pFullName = p.ToDisplayString();
                    dependList.Add((fullName, pFullName));

                    AddSymbolList(symbolList, p);
                }
            }
        }

        //// DBG
        //foreach(var (from, to) in dependList)
        //{
        //    Console.WriteLine($"{from} -> {to}");
        //}

        var umlElementInfos = new Dictionary<string, string>();
        var dotNodeInfos = new Dictionary<string, string>();

        StringBuilder solutionIncludes = new StringBuilder();

        // output PlanUML, Graphviz files
        foreach (var tree in syntaxTrees)
        {
            Console.WriteLine($"listing dependency. path: '{tree.FilePath}'");

            var folderPath = Path.GetDirectoryName(tree.FilePath);

            var codeFileName = Path.GetFileName(tree.FilePath);
            var relativeFolderPath = Path.GetRelativePath(solutionFolder, Path.GetDirectoryName(tree.FilePath));

            var model = compilation.GetSemanticModel(tree);
            var root = tree.GetRoot();
            var myClass = root.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault();

            if (myClass != null)
            {
                var inc = SavePUMLDotFileProcess(model.GetDeclaredSymbol(myClass), folderPath, relativeFolderPath, codeFileName, dependList, symbolList, umlElementInfos, dotNodeInfos);
                solutionIncludes.Append(inc);
            }

            var myInterface = root.DescendantNodes().OfType<InterfaceDeclarationSyntax>().FirstOrDefault();
            if (myInterface != null)
            {
                var inc = SavePUMLDotFileProcess(model.GetDeclaredSymbol(myInterface), folderPath, relativeFolderPath, codeFileName, dependList, symbolList, umlElementInfos, dotNodeInfos);
                solutionIncludes.Append(inc);
            }
        }

        // insert puml, dot files to csproj
        var csprojText = File.ReadAllText(solutionPath);
        var startIndex = csprojText.IndexOf(IMPORT_START);
        var endIndex = csprojText.IndexOf(IMPORT_END);
        if ((startIndex != -1) && (endIndex != -1) && (startIndex < endIndex))
        {
            startIndex += IMPORT_START.Length;
            var bef = csprojText.Substring(0, startIndex);
            var aft = csprojText.Substring(endIndex);
            using (var sw = new StreamWriter(solutionPath))
            {
                sw.WriteLine($"{bef}\r\n{solutionIncludes.ToString()}\r\n{aft}");
            }
        }
        else
        {
            Console.WriteLine("import position not found. please paste blow text in csproj");
            Console.WriteLine($"{IMPORT_START}\r\n{solutionIncludes.ToString()}\r\n{IMPORT_END}\r\n");
        }

        Console.WriteLine("finished.");
    }

    /// <summary>
    /// save puml and dot file
    /// </summary>
    /// <param name="symbol"></param>
    /// <param name="folderPath"></param>
    /// <param name="relativeFolderPath"></param>
    /// <param name="modeledFileName"></param>
    /// <param name="dependList"></param>
    /// <param name="symbolList"></param>
    /// <param name="umlElementInfos"></param>
    /// <param name="dotNodeInfos"></param>
    /// <returns>solution None elements</returns>
    private static string SavePUMLDotFileProcess(INamedTypeSymbol symbol, string folderPath, string relativeFolderPath, string modeledFileName, List<(string from, string to)> dependList, Dictionary<string, INamedTypeSymbol> symbolList, Dictionary<string, string> umlElementInfos, Dictionary<string, string> dotNodeInfos)
    {
        var includes = new StringBuilder();

        var baseInclude = """
    <None Include="%myfile%" ExcludeFromSingleFile="true">
        <DependentUpon>%cs%</DependentUpon>
    </None>
""";

        var symbolName = symbol.Name;

        (var puml, var dot) = CreatePUMLDotDependProcess(symbol, dependList, symbolList, umlElementInfos, dotNodeInfos);
        if (!string.IsNullOrEmpty(puml))
        {
            var pumlPath = Path.Combine(folderPath, $"{symbolName}.puml");
            var dotPath = Path.Combine(folderPath, $"{symbolName}.dot");

            using (var sw = new StreamWriter(pumlPath))
            {
                sw.Write(puml);
            }
            using (var sw = new StreamWriter(dotPath))
            {
                sw.Write(dot);
            }

            includes.Append(baseInclude.Replace("%myfile%", $"{relativeFolderPath}\\{symbolName}.puml")
                                .Replace("%cs%", modeledFileName));
            includes.Append("\r\n");
            includes.Append(baseInclude.Replace("%myfile%", $"{relativeFolderPath}\\{symbolName}.dot")
                                .Replace("%cs%", modeledFileName));
            includes.Append("\r\n");
        }

        (var lmup, var tod) = CreateReversePUMLDotDependProcess(symbol, dependList, symbolList, umlElementInfos, dotNodeInfos);
        if (!string.IsNullOrEmpty(lmup))
        {
            var pumlPath = Path.Combine(folderPath, $"{symbolName}{REVERSE_FILE_POSTFIX}.puml");
            var dotPath = Path.Combine(folderPath, $"{symbolName}{REVERSE_FILE_POSTFIX}.dot");

            using (var sw = new StreamWriter(pumlPath))
            {
                sw.Write(lmup);
            }
            using (var sw = new StreamWriter(dotPath))
            {
                sw.Write(tod);
            }

            includes.Append(baseInclude.Replace("%myfile%", $"{relativeFolderPath}\\{symbolName}{REVERSE_FILE_POSTFIX}.puml")
                                .Replace("%cs%", modeledFileName));
            includes.Append("\r\n");
            includes.Append(baseInclude.Replace("%myfile%", $"{relativeFolderPath}\\{symbolName}{REVERSE_FILE_POSTFIX}.dot")
                                .Replace("%cs%", modeledFileName));
            includes.Append("\r\n");
        }

        return includes.ToString();
    }

    /// <summary>
    /// create puml and dot depends
    /// </summary>
    /// <param name="symbol"></param>
    /// <param name="dependList"></param>
    /// <param name="symbolList"></param>
    /// <param name="umlElementInfos"></param>
    /// <param name="dotNodeInfos"></param>
    /// <returns></returns>
    private static (string, string) CreatePUMLDotDependProcess(INamedTypeSymbol symbol, List<(string from, string to)> dependList, Dictionary<string, INamedTypeSymbol> symbolList, Dictionary<string, string> umlElementInfos, Dictionary<string, string> dotNodeInfos)
    {
        var symbolFullName = symbol?.ToDisplayString() ?? string.Empty;

        var puml = new StringBuilder();
        puml.Append("""
@startuml
left to right direction
%elemdefs%
""");
        var elemDefs = new HashSet<string>();

        var dot = new StringBuilder();
        dot.Append($"digraph \"{symbolFullName}\" ");
        dot.Append("""
{
    rankdir="TB";
    node [
        shape="box",
        fontname="Meiryo UI",
        fontsize="12pt"
    ];

%nodedefs%
""");
        var dotNodeDefs = new HashSet<string>();

        var deps = dependList.Where(item => (item.from == symbolFullName)).ToList();
        if (deps.Count == 0)
        {
            return (string.Empty, string.Empty);
        }

        var processed = new HashSet<string>();

        var nextParents = new HashSet<string>();
        foreach (var (from, to) in deps)
        {
            Console.WriteLine($"{from} => {to}");

            AddPUML(puml, elemDefs, from, to, symbolList, umlElementInfos);
            AddDot(dot, dotNodeDefs, from, to, symbolList, dotNodeInfos);

            nextParents.Add(to);
            processed.Add(to);
        }

        while (nextParents.Count > 0)
        {
            var newParents = new HashSet<string>();
            foreach (var p in nextParents)
            {
                var pdeps = dependList.Where(item => (item.from == p)).ToList();

                foreach (var (from, to) in pdeps)
                {
                    Console.WriteLine($"{from} => {to}");

                    AddPUML(puml, elemDefs, from, to, symbolList, umlElementInfos);
                    AddDot(dot, dotNodeDefs, from, to, symbolList, dotNodeInfos);

                    if (!nextParents.Contains(to) && !newParents.Contains(to) && !processed.Contains(to))
                    {
                        newParents.Add(to);
                    }
                }
            }

            nextParents = newParents;
        }

        puml.Append("@enduml");
        dot.Append('}');

        // concat element defs
        var elemInfo = new StringBuilder();
        foreach (var elem in elemDefs)
        {
            elemInfo.Append($"{elem}\r\n");
        }
        puml.Replace("%elemdefs%", elemInfo.ToString());

        // concat node styles
        var dotNodeStyle = new StringBuilder();
        foreach (var node in dotNodeDefs)
        {
            dotNodeStyle.Append($"{node}\r\n");
        }
        dotNodeStyle.Append("\r\n");
        dot.Replace("%nodedefs%", dotNodeStyle.ToString());

        return (puml.ToString(), dot.ToString());
    }

    /// <summary>
    /// create reverse puml and dot depends
    /// </summary>
    /// <param name="symbol"></param>
    /// <param name="dependList"></param>
    /// <param name="symbolList"></param>
    /// <param name="umlElementInfos"></param>
    /// <param name="dotNodeInfos"></param>
    /// <returns></returns>
    private static (string, string) CreateReversePUMLDotDependProcess(INamedTypeSymbol symbol, List<(string from, string to)> dependList, Dictionary<string, INamedTypeSymbol> symbolList, Dictionary<string, string> umlElementInfos, Dictionary<string, string> dotNodeInfos)
    {
        var symbolFullName = symbol?.ToDisplayString() ?? string.Empty;

        var puml = new StringBuilder();
        puml.Append("""
@startuml
left to right direction
%elemdefs%
""");
            
        var elemDefs = new HashSet<string>();

        var dot = new StringBuilder();
        dot.Append($"digraph \"{symbolFullName}\" ");
        dot.Append("""
{
    rankdir="TB";
    node [
        shape="box",
        fontname="Meiryo UI",
        fontsize="12pt"
    ];

%nodedefs%
""");
        var dotNodeDefs = new HashSet<string>();

        var deps = dependList.Where(item => (item.to == symbolFullName)).ToList();
        if (deps.Count == 0)
        {
            return (string.Empty, string.Empty);
        }

        var processed = new HashSet<string>();

        var nextChildren = new HashSet<string>();
        foreach (var (from, to) in deps)
        {
            Console.WriteLine($"{from} => {to}");

            AddPUML(puml, elemDefs, from, to, symbolList, umlElementInfos);
            AddDot(dot, dotNodeDefs, from, to, symbolList, dotNodeInfos);

            nextChildren.Add(from);
            processed.Add(from);
        }

        while (nextChildren.Count > 0)
        {
            var newChildren = new HashSet<string>();
            foreach (var p in nextChildren)
            {
                var pdeps = dependList.Where(item => (item.to == p)).ToList();

                foreach (var (from, to) in pdeps)
                {
                    Console.WriteLine($"{from} => {to}");

                    AddPUML(puml, elemDefs, from, to, symbolList, umlElementInfos);
                    AddDot(dot, dotNodeDefs, from, to, symbolList, dotNodeInfos);

                    if (!nextChildren.Contains(from) && !newChildren.Contains(from) && !processed.Contains(from))
                    {
                        newChildren.Add(from);
                    }
                }
            }

            nextChildren = newChildren;
        }

        puml.Append("@enduml");
        dot.Append('}');

        // concat element defs
        var elemInfo = new StringBuilder();
        foreach (var elem in elemDefs)
        {
            elemInfo.Append($"{elem}\r\n");
        }
        puml.Replace("%elemdefs%", elemInfo.ToString());

        // concat node styles
        var dotNodeStyle = new StringBuilder();
        foreach (var node in dotNodeDefs)
        {
            dotNodeStyle.Append($"{node}\r\n");
        }
        dotNodeStyle.Append("\r\n");
        dot.Replace("%nodedefs%", dotNodeStyle.ToString());

        return (puml.ToString(), dot.ToString());
    }

    /// <summary>
    /// add puml
    /// </summary>
    /// <param name="puml"></param>
    /// <param name="fromName"></param>
    /// <param name="toName"></param>
    /// <param name="symbols"></param>
    /// <param name="elementInfos"></param>
    private static void AddPUML(StringBuilder puml, HashSet<string> elems, string fromName, string toName, Dictionary<string, INamedTypeSymbol> symbols, Dictionary<string, string> elementInfos)
    {
        var from = symbols[fromName];
        var to = symbols[toName];

        void ElementProcess(string targetName, INamedTypeSymbol sym)
        {
            string newElement;
            if (elementInfos.ContainsKey(targetName))
            {
                newElement = elementInfos[targetName];
            }
            else
            {
                newElement = GetElementInformation(sym);
                elementInfos.Add(targetName, newElement);
            }
            elems.Add(newElement);
        }

        //elems.Add(GetElementInformation(from));
        //elems.Add(GetElementInformation(to));
        ElementProcess(fromName, from);
        ElementProcess(toName, to);

        string arrow = "--";
        switch (from.TypeKind)
        {
            case TypeKind.Class:
                switch (to.TypeKind)
                {
                    case TypeKind.Class:
                        arrow = "--|>";
                        break;
                    case TypeKind.Interface:
                        arrow = "..|>";
                        break;
                          
                }
                break;

            case TypeKind.Interface:
                switch (to.TypeKind)
                {
                    case TypeKind.Class:
                        arrow = "--";
                        break;
                    case TypeKind.Interface:
                        arrow = "--|>";
                        break;
                }
                break;
        }

        puml.Append($"{from.ToDisplayString()} {arrow} {to.ToDisplayString()}\r\n");
    }

    /// <summary>
    /// get PlantUML element info
    /// </summary>
    /// <param name="sym"></param>
    /// <returns></returns>
    private static string GetElementInformation(INamedTypeSymbol sym)
    {
        var ret = new StringBuilder();

        ret.Append($"package \"{sym.ContainingNamespace}\"");
        ret.Append("{\r\n");
        switch (sym.TypeKind)
        {
            case TypeKind.Interface:
                ret.Append($"Interface \"{sym.Name}\" ");
                break;
                
            case TypeKind.Class:
                string t = string.Empty;
                if (sym.IsAbstract)
                {
                    t += $"abstract class \"{sym.Name}\"";
                }
                else
                {
                    t += $"class \"{sym.Name}\" ";
                }

                if (sym.IsSealed)
                {
                    t += "<<sealed>> ";
                }
                if (sym.IsStatic)
                {
                    t += "<<static>> ";
                }

                ret.Append(t);
                break;

            default: // not supported
                return string.Empty;
        }

        ret.Append("{\r\n");

        static char GetAccesibility(Accessibility ac)
        {
            switch (ac)
            {
                case Accessibility.Public:
                    return '+';
                case Accessibility.Protected:
                    return '#';
                case Accessibility.Private:
                    return '-';
                default:
                    return ' ';
            }
        }
        
        // add methods
        var methods = sym.GetMembers().OfType<IMethodSymbol>();
        foreach (var me in methods)
        {
            switch (me.MethodKind)
            {
                case MethodKind.Constructor:
                case MethodKind.PropertyGet:
                case MethodKind.PropertySet:
                    continue;
            }

            ret.Append(GetAccesibility(me.DeclaredAccessibility));

            if (me.IsAbstract)
            {
                ret.Append("{abstract} ");
            }
            if (me.IsStatic)
            {
                ret.Append("{static} ");
            }

            ret.Append($"{me.Name}() : ");

            if (me.ReturnsVoid)
            {
                ret.Append($"void ");
            }
            else
            {
                ret.Append($"{me.ReturnType.Name}");
            }

            if (me.IsAbstract)
            {
                ret.Append("<<abstract>> ");
            }

            ret.Append("\r\n");
        }

        // properties
        var props = sym.GetMembers().OfType<IPropertySymbol>();
        foreach (var prop in props)
        {
            var g = prop.GetMethod;
            var s = prop.SetMethod;

            ret.Append(GetAccesibility(g.DeclaredAccessibility));

            if (prop.IsAbstract)
            {
                ret.Append("{abstract} ");
            }
            if (prop.IsStatic)
            {
                ret.Append("{static} ");
            }

            ret.Append($"{prop.Name} ");

            ITypeSymbol tp = null;
            if (g != null)
            {
                tp = g.ReturnType;
            }
            else if (s != null)
            {
                var pa = s.Parameters;
                if (pa.Length > 1)
                {
                    tp = pa[0].Type;
                }
            }

            if (tp != null)
            {
                ret.Append($": {tp.Name} ");
            }
            if (g != null)
            {
                ret.Append("<<get>> ");
            }
            if (s != null)
            {
                switch (s.DeclaredAccessibility)
                {
                    case Accessibility.Private:
                        ret.Append("<<private set>>");
                        break;
                    case Accessibility.Protected:
                        ret.Append("<<protected set>>");
                        break;
                    case Accessibility.Public:
                    default:
                        ret.Append("<<set>> ");
                        break;
                }
            }

            if (prop.IsAbstract)
            {
                ret.Append("<<abstract>> ");
            }

            ret.Append("\r\n");
        }

        // fields
        var fields = sym.GetMembers().OfType<IFieldSymbol>();
        foreach (var field in fields)
        {
            if (field.Name.EndsWith(">k__BackingField"))
            {
                continue;
            }

            ret.Append(GetAccesibility(field.DeclaredAccessibility));

            ret.Append($"{field.Name} : {field.Type}\r\n");
        }

        ret.Append("}\r\n");

        ret.Append("}\r\n");

        return ret.ToString();
    }

    /// <summary>
    /// add dot
    /// </summary>
    /// <param name="dot"></param>
    /// <param name="nodes"></param>
    /// <param name="fromName"></param>
    /// <param name="toName"></param>
    /// <param name="symbols"></param>
    /// <param name="nodeInfos"></param>
    private static void AddDot(StringBuilder dot, HashSet<string> nodes, string fromName, string toName, Dictionary<string, INamedTypeSymbol> symbols, Dictionary<string, string> nodeInfos)
    {
        var from = symbols[fromName];
        var to = symbols[toName];

        void NodeProcess(string targetName, INamedTypeSymbol sym)
        {
            string newNode;
            if (nodeInfos.ContainsKey(targetName))
            {
                newNode = nodeInfos[targetName];
            }
            else
            {
                newNode = GetDotNodeStyle(sym);
                nodeInfos.Add(targetName, newNode);
            }
            nodes.Add(newNode);
        }

        //nodes.Add(GetDotNodeStyle(from));
        //nodes.Add(GetDotNodeStyle(to));
        NodeProcess(fromName, from);
        NodeProcess(toName, to);

        dot.Append($"\"{from.ToDisplayString()}\" -> \"{to.ToDisplayString()}\";\r\n");
    }

    /// <summary>
    /// get dot node style
    /// </summary>
    /// <param name="sym"></param>
    /// <returns></returns>
    private static string GetDotNodeStyle(INamedTypeSymbol sym)
    {
        var nodeStyle = $"\"{sym.ToDisplayString()}\" [ label=\"{sym.Name}\", ";
        if (sym.TypeKind == TypeKind.Interface)
        {
            nodeStyle += "shape=\"ellipse\", style=\"dashed\", ";
        }
        else if (sym.TypeKind == TypeKind.Class)
        {
            if (sym.IsAbstract)
            {
                nodeStyle += "style=\"dashed\", ";
            }
            if (sym.IsSealed)
            {
                nodeStyle += "shape=\"parallelogram\", style=\"bold\", ";
            }
        }
        
        nodeStyle += "];";

        return nodeStyle;
    }

    /// <summary>
    /// get csproj path
    /// </summary>
    /// <param name="folderPath"></param>
    /// <returns></returns>
    private static string GetCsprojPath(string folderPath)
    {
        var files = Directory.GetFiles(folderPath, "*.csproj");
        return (files.Length > 0) ? files[0] : string.Empty;
    }
}
