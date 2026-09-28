using System.Runtime.CompilerServices;

// Permite aos testes (OperCAD.Tests) validarem o fluxo real de renderização do app,
// que usa os membros internos FindJumpsForConductor/BuildSegments.
[assembly: InternalsVisibleTo("OperCAD.Tests")]
