# Variables and text

Every sub-action text parameter (everything except code) is resolved before the sub-action runs.

## Variables

| Syntax | Meaning |
| --- | --- |
| `%name%` | Action argument (case-insensitive), else a built-in below |
| `~name~` | Global variable (persisted or temporary) |
| `%name:format%`, `~name:format~` | .NET format specifier: `%amount:c2%`, `%n:N0%`, `%date:yyyy-MM-dd%`, `%time:t%` |
| `%name:format\|culture%` | Format with a specific culture: `%amount:c2\|de-DE%` |

Names use letters, digits, `_` and `.`; unknown names are left as written (so `100%` survives).

Formats use the text culture (`TextEnvironment.Culture`), by default the invariant culture with `$` as the currency
symbol. Values without a format are always written invariant (`5.5`, `true`). How formats treat values:

- numbers and dates: formatted;
- text that is a number (`"5.5"`) or a date/time (`"2026-01-02T03:04:05Z"`): formatted as that number/date;
- other text and booleans: inserted unchanged (the format is ignored);
- an invalid format (e.g. `X` on 5.5): the marker stays as written and a warning is logged once.

### Built-ins

`%date%` (short date), `%time%` (short time), `%longtime%`, `%datetime%`, `%unixtime%`, `%unixtimems%`,
`%filedatetime%` (`yyyyMMdd_HHmmss`), `%lf%`, `%cr%`, `%crlf%`, `%tab%`, `%random%` (1-100), `%random:a-b%`,
`%randomdecimal%` (0-1), `%guid%`. `%date:fmt%` / `%time:fmt%` format the current local time. Arguments with the
same name win.

## Inline functions

| Function | Result |
| --- | --- |
| `$math(expr)$` | mXparser 4.4 syntax, as Streamer.bot documents it: `+ - * / ^ #` (modulo), `n!`, `n%` (percent; `a % b` between two operands stays modulo), `^^`, relations `= == != <> ~= < <= > >=`, boolean `& && \| \|\| ~ ! (+) --> <-- <->` and friends, bitwise `@& @\| @^ @<< @>> @~` (1/0 results); literals `b.1010 o.17 h.1F b16.1F 1_2 2_3_4`; `pi e [phi] [gam] [true] [false] [NaN]`; random variables `[Uni] [Int] [Int1]..[Int9] [nat] [Nat] [Nor]`; functions such as `sin cos tg ctg sec asin acos atan sinh rad deg sqrt root abs sgn round(x,n) floor ceil ln log2 lg log(base,x) exp mod C(n,k) fact Gamma Fib ispr not if iff min max gcd lcm add multi mean med mode var std rUni rUnid rNor rList and or xor`; iterated `sum(i, 1, 10, i^2) prod avg vari stdi mini maxi` (`i, from, to, expr[, step]`); calculus `der(expr, x, point) der- der+ int(expr, x, a, b) solve(expr, x, a, b) diff difb`; units and constants in SI units (`10*[km]/[h]`, `[min] [kg] [kB] [%] [k] [c] [G.] [g] [hP] [ly] [au] [Earth-M]`…); special functions `erf erfc erfInv Ei li Li Bell Pi LambW0 LambW1 logGamma diGamma GammaP GammaQ Beta BetaI pNor cNor qNor Stirl2 Euler ConFrac ConPol`…, plus Cuebot's `trunc sign pow clamp random() random(a,b)`. `if`/`iff` only evaluate the branch they return. Arguments may be separated by `,` or `;`. Note `log(a, b)` is mXparser's log of b to base a. An error (syntax, division by zero, unknown name) is reported instead of mXparser's `NaN`. Full list: `MathEvaluator.cs` |
| `$length(text)$` | Length in characters (emoji count once) |
| `$parse(name)$` | Value of the variable whose name is built at run time: `$parse(line%i%)$`; `%name%` / `~name~` pick arguments or globals; `name:format` allowed |
| `$upper(t)$` `$lower(t)$` `$trim(t)$` `$urlencode(t)$` | Text helpers |
| `$replace(text,find,replacement)$` | Case-sensitive replace |
| `$substring(text,start[,length])$` | Negative start counts from the end; ranges are clamped |
| `$pick(a,b,c)$` | One argument at random |

Order: variables are substituted first, then functions run innermost first (`$upper($lower(X)$)$`).
The structure comes from the template only: values inserted from arguments or globals are never parsed again,
so a chat message containing `%x%` or `$math(...)$` stays plain text. Use `$parse()$` for deliberate indirection.

Escapes: `\$name(` writes a literal function call; inside function arguments `\,` `\(` `\)` write literal characters.
Single-argument functions take everything between the parentheses, commas included. Arguments are not trimmed.

Errors (bad syntax, division by zero, unknown variable in `$parse`, wrong argument count): the call stays in the
text with its variables substituted, and a warning is logged once per distinct problem.

## Stores

- `globals.json`: persisted globals, `{ "name": value }` with JSON-typed values (old all-string files still load).
- `user-variables.json`: `{ "version": 1, "users": [ { "platform": "twitch", "userId": "123", "userName": "Alice",
  "variables": { "points": 10 }, "lastWrite": "..." } ] }`. Temporary values live in memory only.
- Auto Type turns `"5"` into 5, `"1.50"` into 1.50 (scale kept), `"true"` into true; `"007"`, `" 5"`, `"1e5"` stay text.
  Increment/Decrement treat missing values as 0 and keep integers integral.
