// The whole suite talks to a handful of libraries. Declaring them once here keeps
// every test file down to its own logic, and stops a missing using from turning
// into sixty-five compiler errors at once.
//
// Only the namespaces the tests actually touch are global. A wildcard over every
// Pharos namespace collides on `World`, `Player` and `Client`, which is worse
// than writing the using where it is needed.
global using Xunit;

global using Zaldaryon.Pharos.Bootstrap;
global using Zaldaryon.Pharos.Core;
global using Zaldaryon.Pharos.Culling;
global using Zaldaryon.Pharos.Graphics;
global using Zaldaryon.Pharos.Inspection;
global using Zaldaryon.Pharos.Memory;
global using Zaldaryon.Pharos.Performance;
global using Zaldaryon.Pharos.Translations;
global using Zaldaryon.Pharos.UI;
global using Zaldaryon.Pharos.XUnit;

global using Vintagestory.API.Common;
global using Vintagestory.API.MathTools;