use std::collections::HashMap;
use std::fmt;
use std::sync::LazyLock;

pub const BASE_COUNT: usize = 8;

/// SI base units, plus the byte for information.
pub const BASE_SYMBOLS: [&str; BASE_COUNT] = ["m", "kg", "s", "A", "K", "mol", "cd", "B"];

/// A dimension: exponents of the base units, stored doubled so `sqrt` of m² (or of m) stays exact.
#[derive(Clone, Copy, PartialEq, Eq, Hash, Debug, Default)]
pub struct Dimension {
    doubled_exponents: [i32; BASE_COUNT],
}

impl Dimension {
    pub const NONE: Dimension = Dimension { doubled_exponents: [0; BASE_COUNT] };

    pub fn base(index: usize, exponent: i32) -> Dimension {
        let mut doubled_exponents: [i32; BASE_COUNT] = [0; BASE_COUNT];
        doubled_exponents[index] = 2 * exponent;
        Dimension { doubled_exponents }
    }

    pub fn is_none(&self) -> bool {
        self.doubled_exponents.iter().all(|exponent| *exponent == 0)
    }

    pub fn multiply(&self, other: &Dimension) -> Dimension {
        self.combine(other, |left, right| left + right)
    }

    pub fn divide(&self, other: &Dimension) -> Dimension {
        self.combine(other, |left, right| left - right)
    }

    /// None when the result would need a non-half-integer exponent (e.g. the cube root of m).
    pub fn power(&self, exponent: f64) -> Option<Dimension> {
        if self.is_none() {
            return Some(*self);
        }
        let mut doubled_exponents: [i32; BASE_COUNT] = [0; BASE_COUNT];
        for index in 0..BASE_COUNT {
            let doubled: f64 = self.doubled_exponents[index] as f64 * exponent;
            if doubled.is_nan() || (doubled - doubled.round_ties_even()).abs() > 1e-9 {
                return None;
            }
            doubled_exponents[index] = doubled.round_ties_even() as i32;
        }
        Some(Dimension { doubled_exponents })
    }

    fn combine(&self, other: &Dimension, combine: impl Fn(i32, i32) -> i32) -> Dimension {
        let mut doubled_exponents: [i32; BASE_COUNT] = [0; BASE_COUNT];
        for index in 0..BASE_COUNT {
            doubled_exponents[index] = combine(self.doubled_exponents[index], other.doubled_exponents[index]);
        }
        Dimension { doubled_exponents }
    }

    fn format_exponent(doubled: i32) -> String {
        if doubled == 2 {
            return String::new();
        }
        if doubled % 2 == 1 {
            return format!("^({doubled}/2)");
        }
        const SUPERSCRIPTS: [char; 10] = ['⁰', '¹', '²', '³', '⁴', '⁵', '⁶', '⁷', '⁸', '⁹'];
        (doubled / 2).to_string().chars().map(|digit| SUPERSCRIPTS[digit as usize - '0' as usize]).collect()
    }
}

/// "kg·m²/s²", or "1" when dimensionless.
impl fmt::Display for Dimension {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        let mut numerator: String = String::new();
        let mut denominator: String = String::new();
        // Conventional order: kg m s A K mol cd B
        for index in [1usize, 0, 2, 3, 4, 5, 6, 7] {
            let doubled: i32 = self.doubled_exponents[index];
            if doubled == 0 {
                continue;
            }
            let target: &mut String = if doubled > 0 { &mut numerator } else { &mut denominator };
            if !target.is_empty() {
                target.push('·');
            }
            target.push_str(BASE_SYMBOLS[index]);
            target.push_str(&Dimension::format_exponent(doubled.abs()));
        }

        if numerator.is_empty() && denominator.is_empty() {
            return write!(formatter, "1");
        }
        let top: &str = if numerator.is_empty() { "1" } else { &numerator };
        if denominator.is_empty() { write!(formatter, "{top}") } else { write!(formatter, "{top}/{denominator}") }
    }
}

/// The unit side of one value component. A bare number literal is adoptable: in sums and comparisons it takes the
/// other operand's unit instead of being an error.
#[derive(Clone, Copy, PartialEq, Eq, Hash, Debug)]
pub struct UnitTag {
    pub dimension: Dimension,
    pub is_adoptable: bool,
}

impl UnitTag {
    pub const BARE: UnitTag = UnitTag { dimension: Dimension::NONE, is_adoptable: true };

    pub const DIMENSIONLESS: UnitTag = UnitTag { dimension: Dimension::NONE, is_adoptable: false };

    pub fn of(dimension: Dimension) -> UnitTag {
        UnitTag { dimension, is_adoptable: false }
    }
}

/// A unit usable in calculator literals (`3 km`): its factor to SI and its dimension.
#[derive(Clone, Debug)]
pub struct UnitDefinition {
    /// The spelling it is written with: km, kilometer, kilometers.
    pub name: String,
    /// Long name and what it measures, for completion.
    pub description: String,
    pub to_si: f64,
    pub dimension: Dimension,
    /// Offered by completion: symbols and plain names, not plurals or prefixed long names.
    pub is_listed: bool,
}

#[derive(Clone, Copy, PartialEq)]
enum Prefixes {
    None,
    /// All SI prefixes, y to Y.
    Decimal,
    /// m µ n p f a z y (the second).
    Small,
    /// k to Y and Ki to Yi (bits and bytes).
    Information,
}

/// (symbol, name, factor) of the SI prefixes. µ is also written u.
const DECIMAL_PREFIXES: [(&str, &str, f64); 20] = [
    ("y", "yocto", 1e-24),
    ("z", "zepto", 1e-21),
    ("a", "atto", 1e-18),
    ("f", "femto", 1e-15),
    ("p", "pico", 1e-12),
    ("n", "nano", 1e-9),
    ("µ", "micro", 1e-6),
    ("m", "milli", 1e-3),
    ("c", "centi", 1e-2),
    ("d", "deci", 1e-1),
    ("da", "deca", 1e1),
    ("h", "hecto", 1e2),
    ("k", "kilo", 1e3),
    ("M", "mega", 1e6),
    ("G", "giga", 1e9),
    ("T", "tera", 1e12),
    ("P", "peta", 1e15),
    ("E", "exa", 1e18),
    ("Z", "zetta", 1e21),
    ("Y", "yotta", 1e24),
];

const BINARY_PREFIXES: [(&str, &str, f64); 8] = [
    ("Ki", "kibi", 1024.0),
    ("Mi", "mebi", 1048576.0),
    ("Gi", "gibi", 1073741824.0),
    ("Ti", "tebi", 1099511627776.0),
    ("Pi", "pebi", 1125899906842624.0),
    ("Ei", "exbi", 1152921504606846976.0),
    ("Zi", "zebi", 1180591620717411303424.0),
    ("Yi", "yobi", 1208925819614629174706176.0),
];

/// One entry of the table: the unit before prefixes.
struct BaseUnit {
    symbol: &'static str,
    name: &'static str,
    /// Empty when the name has no plural form (radian and degree would shadow the intrinsics' names).
    plural: &'static str,
    description: &'static str,
    to_si: f64,
    dimension: Dimension,
    prefixes: Prefixes,
}

fn base_unit(
    symbol: &'static str,
    name: &'static str,
    plural: &'static str,
    description: &'static str,
    to_si: f64,
    dimension: Dimension,
    prefixes: Prefixes,
) -> BaseUnit {
    BaseUnit { symbol, name, plural, description, to_si, dimension, prefixes }
}

/// Translated from kalk's units.kalk (third_party/kalk), with its data errors fixed (becquerel is 1/s, not J/kg;
/// the newton and acre descriptions), ShaderCalc's spellings kept (min, sr, ASCII u for µ), bytes as the
/// information base (kalk uses bits) and nit added for rendering.
fn base_units() -> Vec<BaseUnit> {
    use Prefixes::*;
    let length: Dimension = Dimension::base(0, 1);
    let mass: Dimension = Dimension::base(1, 1);
    let time: Dimension = Dimension::base(2, 1);
    let current: Dimension = Dimension::base(3, 1);
    let amount: Dimension = Dimension::base(5, 1);
    let luminous: Dimension = Dimension::base(6, 1);
    let information: Dimension = Dimension::base(7, 1);
    let area: Dimension = length.multiply(&length);
    let volume: Dimension = area.multiply(&length);
    let frequency: Dimension = Dimension::NONE.divide(&time);
    let force: Dimension = mass.multiply(&length).divide(&time).divide(&time);
    let energy: Dimension = force.multiply(&length);
    let power: Dimension = energy.divide(&time);
    let charge: Dimension = current.multiply(&time);
    let voltage: Dimension = power.divide(&current);
    let flux: Dimension = voltage.multiply(&time);
    let degree: f64 = std::f64::consts::PI / 180.0;
    vec![
        // SI base units
        base_unit("s", "second", "seconds", "SI time", 1.0, time, Small),
        base_unit("m", "meter", "meters", "SI length", 1.0, length, Decimal),
        base_unit("g", "gram", "grams", "SI mass (kg is the base)", 1e-3, mass, Decimal),
        base_unit("A", "ampere", "amperes", "SI electric current", 1.0, current, Decimal),
        base_unit("K", "kelvin", "kelvins", "SI temperature", 1.0, Dimension::base(4, 1), Decimal),
        base_unit("mol", "mole", "moles", "SI amount of substance", 1.0, amount, Decimal),
        base_unit("cd", "candela", "candelas", "SI luminous intensity", 1.0, luminous, Decimal),
        // SI derived units
        base_unit("Hz", "hertz", "hertz", "frequency", 1.0, frequency, Decimal),
        base_unit("rad", "radian", "", "plane angle", 1.0, Dimension::NONE, None),
        base_unit("sr", "steradian", "steradians", "solid angle", 1.0, Dimension::NONE, None),
        base_unit("N", "newton", "newtons", "force", 1.0, force, Decimal),
        base_unit("Pa", "pascal", "pascals", "pressure, stress", 1.0, force.divide(&area), Decimal),
        base_unit("J", "joule", "joules", "energy, work, heat", 1.0, energy, Decimal),
        base_unit("W", "watt", "watts", "power, radiant flux", 1.0, power, Decimal),
        base_unit("C", "coulomb", "coulombs", "electric charge", 1.0, charge, Decimal),
        base_unit("V", "volt", "volts", "electric potential difference", 1.0, voltage, Decimal),
        base_unit("F", "farad", "farads", "capacitance", 1.0, charge.divide(&voltage), Decimal),
        base_unit("Ω", "ohm", "ohms", "electrical resistance", 1.0, voltage.divide(&current), Decimal),
        base_unit("S", "siemens", "siemens", "electrical conductance", 1.0, current.divide(&voltage), Decimal),
        base_unit("Wb", "weber", "webers", "magnetic flux", 1.0, flux, Decimal),
        base_unit("T", "tesla", "teslas", "magnetic flux density", 1.0, flux.divide(&area), Decimal),
        base_unit("H", "henry", "henries", "inductance", 1.0, flux.divide(&current), Decimal),
        base_unit("lm", "lumen", "lumens", "luminous flux (cd·sr)", 1.0, luminous, Decimal),
        base_unit("lx", "lux", "lux", "illuminance (lm/m²)", 1.0, luminous.divide(&area), Decimal),
        base_unit("Bq", "becquerel", "becquerels", "radioactivity (decays per second)", 1.0, frequency, Decimal),
        base_unit("Gy", "gray", "grays", "absorbed dose", 1.0, energy.divide(&mass), Decimal),
        base_unit("Sv", "sievert", "sieverts", "equivalent dose", 1.0, energy.divide(&mass), Decimal),
        base_unit("kat", "katal", "katals", "catalytic activity", 1.0, amount.divide(&time), Decimal),
        // Non-SI units accepted with the SI
        base_unit("min", "minute", "minutes", "time, 60 s", 60.0, time, None),
        base_unit("h", "hour", "hours", "time, 60 min", 3600.0, time, None),
        base_unit("day", "day", "days", "time, 24 h", 86400.0, time, None),
        base_unit("au", "astronomical_unit", "", "length, 149597870700 m", 149597870700.0, length, None),
        base_unit("deg", "degree", "", "plane angle, π/180 rad", degree, Dimension::NONE, None),
        base_unit("ha", "hectare", "hectares", "area, 10000 m²", 10000.0, area, None),
        base_unit("L", "litre", "litres", "volume, 1 dm³", 1e-3, volume, Decimal),
        base_unit("t", "tonne", "tonnes", "mass, 1000 kg", 1000.0, mass, None),
        base_unit("Da", "dalton", "daltons", "mass, 1.66053906660e-27 kg", 1.66053906660e-27, mass, None),
        base_unit("eV", "electronvolt", "electronvolts", "energy, 1.602176634e-19 J", 1.602176634e-19, energy, Decimal),
        // Information
        base_unit("b", "bit", "bits", "information, 1/8 byte", 0.125, information, Information),
        base_unit("B", "byte", "bytes", "information, 8 bits", 1.0, information, Information),
        // US customary
        base_unit("pica", "pica", "picas", "US length, 127/30 mm", 0.127 / 30.0, length, None),
        base_unit("in", "inch", "inches", "US length, 6 picas", 0.0254, length, None),
        base_unit("ft", "foot", "feet", "US length, 12 in", 0.3048, length, None),
        base_unit("yd", "yard", "yards", "US length, 3 ft", 0.9144, length, None),
        base_unit("mi", "mile", "miles", "US length, 1760 yd", 1609.344, length, None),
        base_unit("acre", "acre", "acres", "US area, 43560 ft²", 4046.8564224, area, None),
        base_unit("Tbsp", "tablespoon", "tablespoons", "US volume, 14.8 mL", 14.8e-6, volume, None),
        base_unit("tsp", "teaspoon", "teaspoons", "US volume, 1/3 Tbsp", 14.8e-6 / 3.0, volume, None),
        // Rendering
        base_unit("nit", "nit", "nits", "luminance (cd/m²)", 1.0, luminous.divide(&area), None),
    ]
}

fn add_unit(
    table: &mut HashMap<String, UnitDefinition>,
    name: String,
    description: String,
    to_si: f64,
    dimension: Dimension,
    is_listed: bool,
) {
    let definition: UnitDefinition = UnitDefinition { name: name.clone(), description, to_si, dimension, is_listed };
    let previous: Option<UnitDefinition> = table.insert(name.clone(), definition);
    assert!(previous.is_none(), "unit '{name}' is defined twice");
}

/// Units known to calculator literals, by every spelling: symbols with their prefixes (km, µs, us, KiB), names
/// (kilometer) and plurals (meters).
pub static UNITS: LazyLock<HashMap<String, UnitDefinition>> = LazyLock::new(|| {
    let mut table: HashMap<String, UnitDefinition> = HashMap::new();
    for unit in base_units() {
        let describe = |name: &str| format!("{name}: {}", unit.description);
        add_unit(&mut table, unit.symbol.to_string(), describe(unit.name), unit.to_si, unit.dimension, true);
        if unit.name != unit.symbol {
            add_unit(&mut table, unit.name.to_string(), describe(unit.name), unit.to_si, unit.dimension, true);
        }
        if !unit.plural.is_empty() && unit.plural != unit.name {
            add_unit(&mut table, unit.plural.to_string(), describe(unit.name), unit.to_si, unit.dimension, false);
        }
        let prefixes: Vec<(&str, &str, f64)> = match unit.prefixes {
            Prefixes::None => Vec::new(),
            Prefixes::Decimal => DECIMAL_PREFIXES.to_vec(),
            Prefixes::Small => DECIMAL_PREFIXES.iter().copied().filter(|(_, _, factor)| *factor < 1e-2).collect(),
            Prefixes::Information => DECIMAL_PREFIXES
                .iter()
                .copied()
                .filter(|(_, _, factor)| *factor >= 1e3)
                .chain(BINARY_PREFIXES.iter().copied())
                .collect(),
        };
        for (prefix_symbol, prefix_name, factor) in prefixes {
            let to_si: f64 = factor * unit.to_si;
            let long_name: String = format!("{prefix_name}{}", unit.name);
            let symbol: String = format!("{prefix_symbol}{}", unit.symbol);
            add_unit(&mut table, symbol, describe(&long_name), to_si, unit.dimension, true);
            add_unit(&mut table, long_name.clone(), describe(&long_name), to_si, unit.dimension, false);
            if prefix_symbol == "µ" {
                add_unit(&mut table, format!("u{}", unit.symbol), describe(&long_name), to_si, unit.dimension, false);
            }
        }
    }
    table
});

/// Named SI units shown next to a result's dimension. Of the ones sharing a dimension, Hz (not Bq) is named, and
/// none for J/kg (Gy, Sv).
pub static DERIVED_NAMES: LazyLock<HashMap<Dimension, &'static str>> = LazyLock::new(|| {
    const NAMED: [&str; 14] = ["Hz", "N", "Pa", "J", "W", "C", "V", "F", "Ω", "S", "Wb", "T", "H", "kat"];
    let mut names: HashMap<Dimension, &'static str> = HashMap::new();
    for unit in base_units() {
        if NAMED.contains(&unit.symbol) {
            names.insert(unit.dimension, unit.symbol);
        }
    }
    // Illuminance and luminance share cd/m² (the steradian is dimensionless)
    names.insert(Dimension::base(6, 1).divide(&Dimension::base(0, 2)), "lx or nit");
    names
});

pub fn is_unit_name(name: &str) -> bool {
    UNITS.contains_key(name)
}
