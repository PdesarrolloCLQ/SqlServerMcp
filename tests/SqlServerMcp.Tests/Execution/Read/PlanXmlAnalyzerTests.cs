using System.Globalization;
using System.Text;
using Microsoft.Data.SqlClient;
using SqlServerMcp.Execution;
using SqlServerMcp.Infrastructure;

namespace SqlServerMcp.Tests.Execution.Read;

public class PlanXmlAnalyzerTests
{
    private const string Plan = """
        <ShowPlanXML xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan" Version="1.564" Build="16.0.4315.2">
         <BatchSequence><Batch><Statements>
          <StmtSimple StatementText="SELECT 1" StatementId="1" StatementType="SELECT" StatementSubTreeCost="1.0" StatementEstRows="100" StatementOptmEarlyAbortReason="TimeOut">
           <QueryPlan>
            <Warnings NoJoinPredicate="true"><PlanAffectingConvert ConvertIssue="Seek Plan" Expression="CONVERT(nvarchar(10),[a].[Id],0)=N'5'"/></Warnings>
            <MissingIndexes>
              <MissingIndexGroup Impact="85.5">
                <MissingIndex Database="[VentasTest]" Schema="[ventas]" Table="[Facturas]">
                  <ColumnGroup Usage="EQUALITY"><Column Name="[ClienteId]" ColumnId="2"/></ColumnGroup>
                  <ColumnGroup Usage="INEQUALITY"><Column Name="[Total]" ColumnId="4"/></ColumnGroup>
                  <ColumnGroup Usage="INCLUDE"><Column Name="[Fecha]" ColumnId="3"/><Column Name="[Mi]]Col]" ColumnId="5"/></ColumnGroup>
                </MissingIndex>
              </MissingIndexGroup>
            </MissingIndexes>
            <RelOp NodeId="0" PhysicalOp="Hash Match" LogicalOp="Inner Join" EstimateRows="100" EstimatedTotalSubtreeCost="1.0">
              <Hash>
                <RelOp NodeId="1" PhysicalOp="Clustered Index Scan" LogicalOp="Clustered Index Scan" EstimateRows="5000" EstimatedTotalSubtreeCost="0.6">
                  <Warnings><ColumnsWithNoStatistics><ColumnReference Database="[VentasTest]" Schema="[ventas]" Table="[Facturas]" Column="Total"/></ColumnsWithNoStatistics></Warnings>
                  <IndexScan><Object Database="[VentasTest]" Schema="[ventas]" Table="[Facturas]" Index="[PK_Facturas]"/></IndexScan>
                </RelOp>
                <RelOp NodeId="2" PhysicalOp="Index Seek" LogicalOp="Index Seek" EstimateRows="300" EstimatedTotalSubtreeCost="0.1">
                  <IndexScan><Object Database="[VentasTest]" Schema="[ventas]" Table="[Clientes]" Index="[PK_Clientes]"/></IndexScan>
                </RelOp>
              </Hash>
            </RelOp>
           </QueryPlan>
          </StmtSimple>
         </Statements></Batch></BatchSequence>
        </ShowPlanXML>
        """;

    [Fact]
    public void Analyze_CostosOperadoresPorCostoPropio()
    {
        var result = PlanXmlAnalyzer.Analyze(Plan);

        Assert.Equal(1.0, result.EstimatedCost);
        Assert.Equal(100, result.EstimatedRows);
        Assert.Equal([1, 0, 2], result.CostlyOperators.Select(o => o.NodeId));

        var scan = result.CostlyOperators[0];
        Assert.Equal("Clustered Index Scan", scan.PhysicalOp);
        Assert.Equal(0.6, scan.EstimatedSubtreeCost, 6);
        Assert.Equal(60, scan.CostPercent, 2);
        Assert.Equal(5000, scan.EstimatedRows);
        Assert.Equal("ventas.Facturas (PK_Facturas)", scan.Object);

        var join = result.CostlyOperators[1];
        Assert.Equal(30, join.CostPercent, 2);
        Assert.Null(join.Object);
    }

    [Fact]
    public void Analyze_AdvertenciasEnEspanol()
    {
        var warnings = PlanXmlAnalyzer.Analyze(Plan).Warnings;

        Assert.Contains(warnings, w => w.Contains("producto cartesiano", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains("seek", StringComparison.Ordinal) && w.Contains("CONVERT", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains("sin estadísticas (nodo 1: Clustered Index Scan): ventas.Facturas.Total", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains("tiempo de compilación", StringComparison.Ordinal));
    }

    [Fact]
    public void Analyze_IndiceFaltanteConCreateIndexDelimitado()
    {
        var index = Assert.Single(PlanXmlAnalyzer.Analyze(Plan).MissingIndexes);

        Assert.Equal(85.5, index.Impact);
        Assert.Equal("ventas.Facturas", index.Table);
        Assert.Equal(["ClienteId"], index.EqualityColumns);
        Assert.Equal(["Total"], index.InequalityColumns);
        Assert.Equal(["Fecha", "Mi]Col"], index.IncludeColumns);
        Assert.Equal(
            "CREATE NONCLUSTERED INDEX [IX_Facturas_ClienteId_Total] ON [ventas].[Facturas] ([ClienteId], [Total]) INCLUDE ([Fecha], [Mi]]Col]);",
            index.CreateStatement);
    }

    [Fact]
    public void Analyze_LimitaADiezOperadores()
    {
        var relOps = new StringBuilder();
        for (var i = 15; i >= 1; i--)
        {
            relOps.Insert(0, $"<RelOp NodeId=\"{i}\" PhysicalOp=\"Op{i}\" LogicalOp=\"L\" EstimateRows=\"1\" EstimatedTotalSubtreeCost=\"{(16 - i).ToString(CultureInfo.InvariantCulture)}\"><X>");
            relOps.Append("</X></RelOp>");
        }

        var xml = $"""
            <ShowPlanXML xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan"><BatchSequence><Batch><Statements>
            <StmtSimple StatementSubTreeCost="15" StatementEstRows="1"><QueryPlan>{relOps}</QueryPlan></StmtSimple>
            </Statements></Batch></BatchSequence></ShowPlanXML>
            """;
        var result = PlanXmlAnalyzer.Analyze(xml);
        Assert.Equal(PlanXmlAnalyzer.MaxOperators, result.CostlyOperators.Count);
        Assert.All(result.CostlyOperators, o => Assert.Equal(1.0 / 15 * 100, o.CostPercent, 1));
    }

    [Fact]
    public void Analyze_ProhibeDtdYEntidadesExternas()
    {
        const string malicious = """<?xml version="1.0"?><!DOCTYPE x [<!ENTITY e SYSTEM "file:///c:/windows/win.ini">]><x>&e;</x>""";
        var result = PlanXmlAnalyzer.Analyze(malicious);
        Assert.Contains(result.Warnings, w => w.Contains("No se pudo analizar", StringComparison.Ordinal));
        Assert.Empty(result.CostlyOperators);
    }

    [Fact]
    public void Analyze_DocumentoInesperado()
    {
        var result = PlanXmlAnalyzer.Analyze("<otro/>");
        Assert.Contains(result.Warnings, w => w.Contains("formato SHOWPLAN", StringComparison.Ordinal));
        Assert.Null(result.EstimatedCost);
    }

    [Fact]
    public void BuildBatch_DeclaraSoloNombresYTipos()
    {
        var binder = new ParameterBinder();
        using var doc = System.Text.Json.JsonDocument.Parse("""["O'Brien'; DROP TABLE x --", 5]""");
        var parameters = new[]
        {
            binder.Bind(new QueryParameter("nombre", null, doc.RootElement[0].Clone())),
            binder.Bind(new QueryParameter("@id", "int", doc.RootElement[1].Clone())),
        };

        var batch = ExecutionPlanService.BuildBatch("SELECT 1 WHERE @id = 1", parameters);

        Assert.Equal("DECLARE @nombre nvarchar(4000), @id int; SELECT 1 WHERE @id = 1", batch);
        Assert.DoesNotContain("O'Brien", batch, StringComparison.Ordinal);
        Assert.Equal("SELECT 1", ExecutionPlanService.BuildBatch("SELECT 1", []));
    }

    [Fact]
    public void BuildBatch_RechazaNombresNoValidados()
    {
        var unsafeParameter = new SqlParameter("@x int; DROP TABLE t; --", System.Data.SqlDbType.Int);
        var ex = Assert.Throws<ToolFailureException>(() => ExecutionPlanService.BuildBatch("SELECT 1", [unsafeParameter]));
        Assert.Equal(ErrorCodes.InvalidArgument, ex.Code);
    }

    [Fact]
    public void TruncateUtf8_RespetaBytes()
    {
        Assert.Equal(("abc", false), ExecutionPlanService.TruncateUtf8("abc", 3));
        Assert.Equal(("ab", true), ExecutionPlanService.TruncateUtf8("abc", 2));
        Assert.Equal(("a", true), ExecutionPlanService.TruncateUtf8("añb", 2));
        Assert.Equal(("a", true), ExecutionPlanService.TruncateUtf8("a\U0001F600", 4));
    }
}
