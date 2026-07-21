using System.Collections.Generic;
using ReSet.Core.Models;

namespace ReSet.Core.Services
{
    public class LocalAiConsolidator
    {
        public DeconstructedSpLogic Consolidate(List<DeconstructedSpLogic> chunkResults, SpOverviewInfo globalOverview, List<SpParameterInfo> globalParameters)
        {
            var finalLogic = new DeconstructedSpLogic
            {
                Overview = globalOverview,
                Parameters = globalParameters
            };

            int stepCounter = 1;

            foreach (var chunk in chunkResults)
            {
                if (chunk == null) continue;

                // Merge CRUD
                if (chunk.Crud != null)
                {
                    if (chunk.Crud.SelectTables != null) finalLogic.Crud.SelectTables.AddRange(chunk.Crud.SelectTables);
                    if (chunk.Crud.InsertTables != null) finalLogic.Crud.InsertTables.AddRange(chunk.Crud.InsertTables);
                    if (chunk.Crud.UpdateTables != null) finalLogic.Crud.UpdateTables.AddRange(chunk.Crud.UpdateTables);
                    if (chunk.Crud.DeleteTables != null) finalLogic.Crud.DeleteTables.AddRange(chunk.Crud.DeleteTables);
                    if (chunk.Crud.Udfs != null) finalLogic.Crud.Udfs.AddRange(chunk.Crud.Udfs);

                    if (chunk.Crud.HasTempTables) finalLogic.Crud.HasTempTables = true;
                    if (!string.IsNullOrWhiteSpace(chunk.Crud.TempTablesUsage)) 
                        finalLogic.Crud.TempTablesUsage += chunk.Crud.TempTablesUsage + "\n";

                    if (chunk.Crud.HasLinkedServers) finalLogic.Crud.HasLinkedServers = true;
                    if (!string.IsNullOrWhiteSpace(chunk.Crud.LinkedServersUsage))
                        finalLogic.Crud.LinkedServersUsage += chunk.Crud.LinkedServersUsage + "\n";
                }

                // Merge Logic Steps
                if (chunk.Logic != null)
                {
                    if (chunk.Logic.Steps != null)
                    {
                        foreach (var step in chunk.Logic.Steps)
                        {
                            step.StepNumber = stepCounter++;
                            finalLogic.Logic.Steps.Add(step);
                        }
                    }

                    if (chunk.Logic.ExceptionVulnerabilities != null) finalLogic.Logic.ExceptionVulnerabilities.AddRange(chunk.Logic.ExceptionVulnerabilities);
                    if (chunk.Logic.IsolationImplications != null) finalLogic.Logic.IsolationImplications.AddRange(chunk.Logic.IsolationImplications);
                    if (chunk.Logic.ReturnCodes != null) finalLogic.Logic.ReturnCodes.AddRange(chunk.Logic.ReturnCodes);
                    if (chunk.Logic.ParameterValidation != null) finalLogic.Logic.ParameterValidation.AddRange(chunk.Logic.ParameterValidation);
                }

                // Merge Visualization Nodes and Links
                if (chunk.Visualization != null)
                {
                    if (chunk.Visualization.Nodes != null) finalLogic.Visualization.Nodes.AddRange(chunk.Visualization.Nodes);
                    if (chunk.Visualization.Links != null) finalLogic.Visualization.Links.AddRange(chunk.Visualization.Links);
                }
            }

            return finalLogic;
        }
    }
}
