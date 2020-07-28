﻿using EFCorePowerTools.Shared.Annotations;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Internal;
using Microsoft.EntityFrameworkCore.Scaffolding;
using RevEng.Core.Procedures.Model;
using RevEng.Core.Procedures.Model.Metadata;
using RevEng.Core.Procedures.Scaffolding;
using ReverseEngineer20;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace RevEng.Core.Procedures
{
    public class SqlServerProcedureScaffolder : IProcedureScaffolder
    {
        private readonly IProcedureModelFactory procedureModelFactory;
        private readonly ICSharpHelper code;

        private IndentedStringBuilder _sb;

        public SqlServerProcedureScaffolder(IProcedureModelFactory procedureModelFactory, [NotNull] ICSharpHelper code)
        {
            this.procedureModelFactory = procedureModelFactory;
            this.code = code;
        }

        public ScaffoldedModel ScaffoldModel(string connectionString, ProcedureScaffolderOptions procedureScaffolderOptions, ProcedureModelFactoryOptions procedureModelFactoryOptions)
        {
            var result = new ScaffoldedModel();

            var model = procedureModelFactory.Create(connectionString, procedureModelFactoryOptions);

            foreach (var procedure in model.Procedures)
            {
                var name = GenerateIdentifierName(procedure.Name) + "Result";

                var classContent = WriteResultClass(procedure, procedureScaffolderOptions.ModelNamespace, name);

                result.AdditionalFiles.Add(new ScaffoldedFile
                {
                    Code = classContent,
                    Path = $"{name}.cs",
                });
            }

            var dbContext = GenerateProcedureDbContext(procedureScaffolderOptions, model);

            result.ContextFile = new ScaffoldedFile
            {
                Code = dbContext,
                Path = Path.GetFullPath(Path.Combine(procedureScaffolderOptions.ContextDir, procedureScaffolderOptions.ContextName + "Procedures.cs")),
            };

            return result;
        }

        public SavedModelFiles Save(ScaffoldedModel scaffoldedModel, string outputDir)
        {
            Directory.CreateDirectory(outputDir);

            var contextPath = Path.GetFullPath(Path.Combine(outputDir, scaffoldedModel.ContextFile.Path));
            Directory.CreateDirectory(Path.GetDirectoryName(contextPath));
            File.WriteAllText(contextPath, scaffoldedModel.ContextFile.Code, Encoding.UTF8);

            var additionalFiles = new List<string>();
            foreach (var entityTypeFile in scaffoldedModel.AdditionalFiles)
            {
                var additionalFilePath = Path.Combine(outputDir, entityTypeFile.Path);
                File.WriteAllText(additionalFilePath, entityTypeFile.Code, Encoding.UTF8);
                additionalFiles.Add(additionalFilePath);
            }

            return new SavedModelFiles(contextPath, additionalFiles);
        }

        private string GenerateProcedureDbContext(ProcedureScaffolderOptions procedureScaffolderOptions, ProcedureModel model)
        {
            _sb = new IndentedStringBuilder();

            _sb.AppendLine(PathHelper.Header);

            //TODO Sort and distinct usings.
            _sb.AppendLine("using Microsoft.Data.SqlClient;");
            _sb.AppendLine("using Microsoft.EntityFrameworkCore;");
            _sb.AppendLine("using System;");
            _sb.AppendLine("using System.Collections.Generic;");
            _sb.AppendLine("using System.Threading.Tasks;");
            _sb.AppendLine($"using {procedureScaffolderOptions.ModelNamespace};");

            _sb.AppendLine();
            _sb.AppendLine($"namespace {procedureScaffolderOptions.ContextNamespace}");
            _sb.AppendLine("{");

            using (_sb.Indent())
            {
                _sb.AppendLine($"public partial class {procedureScaffolderOptions.ContextName}Procedures");

                _sb.AppendLine("{");

                using (_sb.Indent())
                {
                    _sb.AppendLine($"private readonly {procedureScaffolderOptions.ContextName} _context;");
                    _sb.AppendLine();
                    _sb.AppendLine($"public {procedureScaffolderOptions.ContextName}Procedures({procedureScaffolderOptions.ContextName} context)");
                    _sb.AppendLine("{");

                    using (_sb.Indent())
                    {
                        _sb.AppendLine($"_context = context;");
                    }

                    _sb.AppendLine("}");
                }

                foreach (var procedure in model.Procedures)
                {
                    using (_sb.Indent())
                    {
                        _sb.AppendLine();

                        //TODO add support for out parameters
                        var paramStrings = procedure.Parameters
                            .Where(p => !p.Output)
                            .Select(p => $"{code.Reference(p.ClrType())} {p.Name}");

                        _sb.AppendLine($"public async Task<{GenerateIdentifierName(procedure.Name)}Result[]> {GenerateIdentifierName(procedure.Name)}({string.Join(',', paramStrings)})");
                        _sb.AppendLine("{");

                        using (_sb.Indent())
                        {
                            foreach (var parameter in procedure.Parameters)
                            {
                                _sb.AppendLine($"var parameter{parameter.Name} = new SqlParameter");
                                _sb.AppendLine("{");

                                using (_sb.Indent())
                                {
                                    _sb.AppendLine($"ParameterName = \"{parameter.Name}\",");
                                    if (parameter.Precision > 0)
                                    {
                                        _sb.AppendLine($"Precision = {parameter.Precision},");
                                    }
                                    if (parameter.Scale > 0)
                                    {
                                        _sb.AppendLine($"Scale = {parameter.Scale},");
                                    }
                                    if (parameter.Length > 0)
                                    {
                                        _sb.AppendLine($"Size = {parameter.Length},");
                                    }

                                    _sb.AppendLine($"SqlDbType = System.Data.SqlDbType.{parameter.DbType()},");
                                    _sb.AppendLine($"Value = {parameter.Name},");
                                }

                                _sb.AppendLine("};");
                                _sb.AppendLine();
                            }

                            var paramNames = procedure.Parameters
                                .Where(p => !p.Output)
                                .Select(p => $"parameter{p.Name}");

                            var paramProcNames = procedure.Parameters
                                .Where(p => !p.Output)
                                .Select(p => $"@{p.Name}");

                            if (procedure.Parameters.Count == 0)
                            {
                                _sb.AppendLine($"var result = await _context.SqlQuery<{GenerateIdentifierName(procedure.Name)}Result>(\"EXEC [{procedure.Schema}].[{procedure.Name}]\");");
                            }
                            else
                            {
                                _sb.AppendLine($"var result = await _context.SqlQuery<{GenerateIdentifierName(procedure.Name)}Result>(\"EXEC [{procedure.Schema}].[{procedure.Name}] {string.Join(',', paramProcNames)} \", {string.Join(',', paramNames)});");
                            }

                            _sb.AppendLine($"return result;");
                        }

                        _sb.AppendLine("}");
                    }
                }

                _sb.AppendLine("}");
            }

            _sb.AppendLine("}");

            return _sb.ToString();
        }

        private string WriteResultClass(StoredProcedure storedProcedure, string @namespace, string name)
        {
            _sb = new IndentedStringBuilder();

            _sb.AppendLine(PathHelper.Header);
            _sb.AppendLine("using System;");
            _sb.AppendLine("using System.Collections.Generic;");

            _sb.AppendLine();
            _sb.AppendLine($"namespace {@namespace}");
            _sb.AppendLine("{");

            using (_sb.Indent())
            {
                GenerateClass(storedProcedure, name);
            }

            _sb.AppendLine("}");

            return _sb.ToString();
        }

        private void GenerateClass(StoredProcedure storedProcedure, string name)
        {
            _sb.AppendLine($"public partial class {name}");
            _sb.AppendLine("{");

            using (_sb.Indent())
            {
                GenerateProperties(storedProcedure);
            }

            _sb.AppendLine("}");
        }

        private void GenerateProperties(StoredProcedure storedProcedure)
        {
            foreach (var property in storedProcedure.ResultElements.OrderBy(e => e.Ordinal))
            {
                _sb.AppendLine($"public {code.Reference(property.ClrType())} {property.Name} {{ get; set; }}");
            }
        }

        private string GenerateIdentifierName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentNullException(nameof(name));
            }
            var isValid = System.CodeDom.Compiler.CodeGenerator.IsValidLanguageIndependentIdentifier(name);

            if (!isValid)
            {
                // File name contains invalid chars, remove them
                var regex = new Regex(@"[^\p{Ll}\p{Lu}\p{Lt}\p{Lo}\p{Nd}\p{Nl}\p{Mn}\p{Mc}\p{Cf}\p{Pc}\p{Lm}]", RegexOptions.None, TimeSpan.FromSeconds(5));
                name = regex.Replace(name, "");

                // Class name doesn't begin with a letter, insert an underscore
                if (!char.IsLetter(name, 0))
                {
                    name = name.Insert(0, "_");
                }
            }

            return name.Replace(" ", string.Empty);
        }
    }
}
