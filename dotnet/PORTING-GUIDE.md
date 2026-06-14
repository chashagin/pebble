# Pebble Swift → C# Porting Guide (PebbleCore)

Goal: a **bit-for-bit faithful** C# port of the Swift engine. The frozen `goldens/*.json`
are the contract — ported code must reproduce them exactly. When in doubt, translate
literally; do not "improve" logic, reorder operations, or change numeric types.

Target: `net9.0`, `namespace PebbleCore;` (file-scoped), `Nullable` disabled,
`ImplicitUsings` enabled. One C# file per Swift file (same base name).

## Determinism (critical)
- Float math must stay bit-identical. NEVER use `Math.FusedMultiplyAdd`. Keep the exact
  parenthesization/operation order from Swift. RyuJIT x64 uses SSE2 scalar doubles, which
  matches Swift/LLVM — verified: 911 fdlibm probes are bit-identical.
- Simulation trig uses `detSin`/`detCos`/`detAtan`/`detAtan2` (already in DetMath.cs).
  Render-only Float trig uses `MathF.Sin/Cos/Tan`.
- `.rounded(.down)` → `Math.Floor`; `Int(x)` (Double→Int, truncates toward 0) → `(int)x`;
  `(x).rounded()` → `Math.Round(x, MidpointRounding.ToEven)` unless Swift used `.toNearestOrAwayFromZero`.

## Module globals → static classes (surfaced via `global using static`)
Swift module-level `func`/`var`/`let` become `public static` members of a static class so the
rest of the port can call them unqualified. Canonical classes (add `global using static` lines
to GlobalUsings.cs as you create them):
- Core: `DetMath`, `Rng`, `MathX`, `Dirs` (done).
- Block registry: **`Reg`** (in BlockDefs.cs) holds `blockDefs` (List<BlockDef>), `byName`
  (Dictionary<string,int>), `tileNames` (List<string>), `tileMap`, and the free funcs
  `registerBlock, tileId, allTileNames, tileCount, tileName, tex, texTB, texCol, tex6, bid,
  bidOpt, blockName, blockExists, prettify`, plus `public static ResolvedBlockIDs B`.
- Other module-global sets (e.g. world helpers `floorDiv/posMod/chunkKey/dimInfo/DIMS`) go in a
  static class named after the file (e.g. `ChunkGlobals`) OR keep them as `public static` on a
  reasonable home class and add a `global using static`. Prefer one obvious home; note it at the
  top of the file in a comment.

## Type mapping
- `final class` → `class`. `struct` → `class` UNLESS it is a small value type used purely for
  math/data with copy semantics (Vec3, AABB, ItemStack is a **class** in Swift). When unsure
  whether the Swift code relies on value-copy semantics, keep `struct` only if it has no methods
  that mutate through stored references and is never mutated in-place inside a collection element;
  otherwise use `class`. Document the choice.
- `enum E: UInt8 { case a=0, b, c }` → `public enum E : byte { a = 0, b, c }` (same order/values).
- `enum E: String` → `public enum E { ... }`; if `.rawValue` is used as the lowercase name, the
  enum member name already matches — use `e.ToString()`; if raw values differ from names, add a
  `ToRaw()` extension with an explicit switch.
- Swift enum with associated values (e.g. `DropSpec`, `TexSpec`) → a sealed-ish class with a
  private tag enum + payload fields and static factory methods:
  `DropSpec.selfDrop`, `DropSpec.none`, `DropSpec.item(string)`, `DropSpec.list(Drop[])`,
  `DropSpec.fn(Func<int,DropCtx,List<Drop>>)`. Consumers switch on the tag.
- Optionals: reference `T?` → `T` (nullable already). Value `Int?/Bool?/Double?` → `int?/bool?/double?`.
- Closures: `(A,B)->C` → `Func<A,B,C>`; `()->Void` → `Action`; `@escaping`/`@autoclosure` ignored.
- Tuples `(Int, Int)` → C# tuples `(int, int)` (use `.Item1`/named).

## Operators & arithmetic
- Wrapping `&+ &- &* &<< &>>` → wrap the expression in `unchecked(...)`.
- `Int32(truncatingIfNeeded: x)` → `unchecked((int)x)`; `UInt32(truncatingIfNeeded:)` → `(uint)`.
- `x.bitPattern` (Double) → `BitConverter.DoubleToUInt64Bits(x)`; `Double(bitPattern:)` →
  `BitConverter.UInt64BitsToDouble(...)`. Float: `BitConverter.SingleToUInt32Bits` etc.
- `a % b` (Swift truncatingRemainder for Double) → C# `%` (same). Int `%` matches.
- `>>`/`<<` on unsigned are logical in both; on signed are arithmetic in both. OK to keep.

## Collections & stdlib
- `[T]` → `List<T>` (growable) or `T[]` (fixed-size buffers like chunk arrays — use arrays).
- `[K:V]` → `Dictionary<K,V>`; `Set<T>` → `HashSet<T>`.
- `.append`→`.Add`, `.count`→`.Count`/`.Length`, `.isEmpty`→`.Count==0`, `.removeAll`→`.Clear`,
  `.contains`→`.Contains`, `.first(where:)`/`.map`/`.filter`/`.reduce`/`.sorted` → LINQ
  (`using System.Linq;`), `.removeValue(forKey:)`→`.Remove(key)` (bool), subscript get on
  Dictionary returning optional → `TryGetValue`.
- `[T](repeating: v, count: n)` → `Enumerable.Repeat(v,n).ToArray()` or `new T[n]` then fill
  (for value default 0 just `new T[n]`).
- Strings: `\(x)`→`$"{x}"`; `.split(separator:)`→`.Split`; `.hasPrefix/.hasSuffix`→`StartsWith/EndsWith`;
  `.uppercased()`→`.ToUpperInvariant()`; `.prefix(1)`→`s.Substring(0,1)`.

## Control flow & misc
- `guard let x = opt else { return }` → `if (opt == null) return; var x = opt;` (or `.Value`).
- `precondition(cond, msg)` / `fatalError(msg)` → `if (!cond) throw new InvalidOperationException(msg);`
  / `throw new InvalidOperationException(msg);`
- `@inline(__always)` → drop (or `[MethodImpl(MethodImplOptions.AggressiveInlining)]` if hot).
- `@discardableResult` → drop. `inout` params → `ref`.
- `defer` → `try/finally`.
- `Codable` → for save types add `[System.Text.Json] `-friendly plain props; we will wire JSON
  serialization in the Saves layer. For now keep public fields/props.

## Reference: already-ported files
See `dotnet/PebbleCore/Core/*.cs` (DetMath, RandomX, Noise, MathX, Dirs) and
`dotnet/PebbleSmoke/Program.cs` for the exact idioms in practice.

## Output rules for subagents
- Write each C# file to `dotnet/PebbleCore/<SubDir>/<Name>.cs` (mirror the Swift subdir:
  World, Gen, Entity, Items, Systems, Render, Game).
- Do NOT add `global using static` lines yourself; instead, at the very top of each file, list in
  a comment which static class(es) it defines so the orchestrator can wire GlobalUsings.
- Preserve comments that explain determinism/ordering invariants.
- Keep public API names identical to Swift (method/field names) so other files resolve.
