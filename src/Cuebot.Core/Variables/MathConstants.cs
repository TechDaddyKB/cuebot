namespace Cuebot.Core.Variables;

/// <summary>
/// mXparser 4.4's bracketed constants and units (<c>[phi]</c>, <c>[km]</c>, <c>[c]</c>, <c>[Earth-M]</c>…), in SI base
/// units (m, kg, s; bit for information; rad for angles), so <c>10*[km]/[h]</c> is a speed in m/s and
/// <c>3600*[s]/[min]</c> is 60. Names are case-sensitive, like mXparser's. Physical constants are CODATA 2018;
/// planetary values are NASA fact sheet means.
/// </summary>
internal static class MathConstants
{
    private const double Pi = Math.PI;
    private const double Au = 149_597_870_700;
    private const double Parsec = Au * 648_000 / Pi;
    private const double Planck = 6.62607015e-34;
    private const double ElectronVolt = 1.602176634e-19;
    private const double Mile = 1609.344;

    private static readonly Dictionary<string, double> Values = new(StringComparer.Ordinal)
    {
        // mathematical constants
        ["true"] = 1,
        ["false"] = 0,
        ["NaN"] = double.NaN,
        ["gam"] = 0.57721566490153286060651209008240243,
        ["phi"] = 1.61803398874989484820458683436563811,
        ["PN"] = 1.32471795724474602596090885447809734,
        ["B*"] = 0.70258,
        ["F'd"] = 4.66920160910299067185320382046620161,
        ["F'a"] = 2.50290787509589282228390287321821578,
        ["C2"] = 0.66016181584686957392781211001455577,
        ["M1"] = 0.26149721284764278375542683860869585,
        ["B2"] = 1.9021605831040,
        ["B4"] = 0.87058838,
        ["Kat"] = 0.91596559417721901505460351493238411,
        ["K*"] = 0.76422365358922066299069873125009232,
        ["K."] = 1.13198824,
        ["RS'm"] = 1.45136923488338105028396848589202744,
        ["EB'e"] = 1.60669515241529176378330152319092458,
        ["Bern"] = 0.28016949902386913303,
        ["GKW'l"] = 0.30366300289873265859744812190155623,
        ["HSM's"] = 0.35323637185499598454351655043268201,
        ["lm"] = 0.62432998854355087099293638310083724,
        ["Cah"] = 0.6434105462883380261822543077575647,
        ["Ll"] = 0.66274341934918158097474209710925290,
        ["AG"] = 0.80939402054063913071793188059409131,
        ["L*"] = 1.09868580552518701,
        ["L."] = 3.27582291872181115978768188245384386,
        ["Dz3"] = 1.20205690315959428539973816151144999,
        ["A3n"] = 1.30637788386308069046861449260260571,
        ["Bh"] = 1.45607494858268967139959535111654356,
        ["Pt"] = 1.46707807943397547289779848470722995,
        ["L2"] = 1.53960071783900203869106341467188655,
        ["Nv"] = 1.70521114010536776428855145343450816,
        ["Ks"] = 2.58498175957925321706589358738317116,
        ["Kh"] = 2.68545200106530644530971483548179569,
        ["FR"] = 2.80777024202851936522150118655777293,
        ["P2"] = 2.29558714939263807403429804918949039,
        ["Om"] = 0.56714329040978387299996866221035555,
        ["MRB"] = 0.18785964246206712024851793405427323,
        ["li2"] = 1.04516378011749278484458888919461313,
        ["EG"] = 0.59634736232319407434107849936927937,

        // dimensionless
        ["%"] = 0.01,
        ["%%"] = 0.001,
        ["Y"] = 1e24, ["sept"] = 1e24,
        ["Z"] = 1e21, ["sext"] = 1e21,
        ["E"] = 1e18, ["quint"] = 1e18,
        ["P"] = 1e15, ["quad"] = 1e15,
        ["T"] = 1e12, ["tril"] = 1e12,
        ["G"] = 1e9, ["bil"] = 1e9,
        ["M"] = 1e6, ["mil"] = 1e6,
        ["k"] = 1e3, ["th"] = 1e3,
        ["hund"] = 1e2, ["hecto"] = 1e2,
        ["ten"] = 10, ["deca"] = 10,
        ["deci"] = 1e-1,
        ["centi"] = 1e-2,
        ["milli"] = 1e-3,
        ["mic"] = 1e-6,
        ["n"] = 1e-9,
        ["p"] = 1e-12,
        ["f"] = 1e-15,
        ["a"] = 1e-18,
        ["z"] = 1e-21,
        ["y"] = 1e-24,

        // length
        ["m"] = 1,
        ["km"] = 1e3,
        ["cm"] = 1e-2,
        ["mm"] = 1e-3,
        ["inch"] = 0.0254,
        ["yd"] = 0.9144,
        ["ft"] = 0.3048,
        ["mile"] = Mile,
        ["nmi"] = 1852,

        // area
        ["m2"] = 1,
        ["cm2"] = 1e-4,
        ["mm2"] = 1e-6,
        ["are"] = 100,
        ["ha"] = 1e4,
        ["acre"] = 4046.8564224,
        ["km2"] = 1e6,

        // volume (US gallon and pint)
        ["mm3"] = 1e-9,
        ["cm3"] = 1e-6,
        ["m3"] = 1,
        ["km3"] = 1e9,
        ["ml"] = 1e-6,
        ["l"] = 1e-3,
        ["gall"] = 3.785411784e-3,
        ["pint"] = 4.73176473e-4,

        // time
        ["s"] = 1,
        ["ms"] = 1e-3,
        ["min"] = 60,
        ["h"] = 3600,
        ["day"] = 86_400,
        ["week"] = 604_800,
        ["yearj"] = 31_557_600,

        // mass
        ["kg"] = 1,
        ["gr"] = 1e-3,
        ["mg"] = 1e-6,
        ["dag"] = 1e-2,
        ["t"] = 1e3,
        ["oz"] = 0.028349523125,
        ["lb"] = 0.45359237,

        // information (bit = 1, decimal prefixes)
        ["b"] = 1, ["kb"] = 1e3, ["Mb"] = 1e6, ["Gb"] = 1e9, ["Tb"] = 1e12, ["Pb"] = 1e15, ["Eb"] = 1e18, ["Zb"] = 1e21, ["Yb"] = 1e24,
        ["B"] = 8, ["kB"] = 8e3, ["MB"] = 8e6, ["GB"] = 8e9, ["TB"] = 8e12, ["PB"] = 8e15, ["EB"] = 8e18, ["ZB"] = 8e21, ["YB"] = 8e24,

        // energy
        ["J"] = 1,
        ["eV"] = ElectronVolt,
        ["keV"] = ElectronVolt * 1e3,
        ["MeV"] = ElectronVolt * 1e6,
        ["GeV"] = ElectronVolt * 1e9,
        ["TeV"] = ElectronVolt * 1e12,

        // speed and acceleration
        ["m/s"] = 1,
        ["km/h"] = 1000.0 / 3600,
        ["mi/h"] = Mile / 3600,
        ["knot"] = 1852.0 / 3600,
        ["m/s2"] = 1,
        ["km/h2"] = 1000.0 / (3600.0 * 3600),
        ["mi/h2"] = Mile / (3600.0 * 3600),

        // angle
        ["rad"] = 1,
        ["deg"] = Pi / 180,
        ["‘"] = Pi / 10_800,    // ‘ minute of arc
        ["”"] = Pi / 648_000,   // ” second of arc

        // physical constants
        ["c"] = 299_792_458,
        ["G."] = 6.67430e-11,
        ["g"] = 9.80665,
        ["hP"] = Planck,
        ["h-"] = Planck / (2 * Pi),
        ["lP"] = 1.616255e-35,
        ["mP"] = 2.176434e-8,
        ["tP"] = 5.391247e-44,

        // astronomical constants
        ["ly"] = 9_460_730_472_580_800,
        ["au"] = Au,
        ["pc"] = Parsec,
        ["kpc"] = Parsec * 1e3,
        ["Earth-R-eq"] = 6_378_137,
        ["Earth-R-po"] = 6_356_752,
        ["Earth-R"] = 6_371_000,
        ["Earth-M"] = 5.9722e24,
        ["Earth-D"] = 149.598e9,
        ["Moon-R"] = 1_737_400,
        ["Moon-M"] = 7.346e22,
        ["Moon-D"] = 384_400_000,
        ["Solar-R"] = 695_700_000,
        ["Solar-M"] = 1.98847e30,
        ["Mercury-R"] = 2_439_700,
        ["Mercury-M"] = 3.3010e23,
        ["Mercury-D"] = 57.909e9,
        ["Venus-R"] = 6_051_800,
        ["Venus-M"] = 4.8673e24,
        ["Venus-D"] = 108.210e9,
        ["Mars-R"] = 3_389_500,
        ["Mars-M"] = 6.4169e23,
        ["Mars-D"] = 227.956e9,
        ["Jupiter-R"] = 69_911_000,
        ["Jupiter-M"] = 1.89813e27,
        ["Jupiter-D"] = 778.479e9,
        ["Saturn-R"] = 58_232_000,
        ["Saturn-M"] = 5.6832e26,
        ["Saturn-D"] = 1432.041e9,
        ["Uranus-R"] = 25_362_000,
        ["Uranus-M"] = 8.6811e25,
        ["Uranus-D"] = 2867.043e9,
        ["Neptune-R"] = 24_622_000,
        ["Neptune-M"] = 1.02409e26,
        ["Neptune-D"] = 4514.953e9,
    };

    public static bool TryGet(string name, out double value) => Values.TryGetValue(name, out value);
}
