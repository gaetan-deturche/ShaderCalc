use std::collections::HashMap;
use std::fmt;
use std::sync::LazyLock;

pub const BASE_SYMBOLS: [&str; 7] = ["m", "kg", "s", "A", "K", "mol", "cd"];

/// An SI dimension: exponents of the 7 base units, stored doubled so `sqrt` of m² (or of m) stays exact.
#[derive(Clone, Copy, PartialEq, Eq, Hash, Debug, Default)]
pub struct Dimension {
    doubled_exponents: [i32; 7],
}

impl Dimension {
    pub const NONE: Dimension = Dimension { doubled_exponents: [0; 7] };

    pub fn base(index: usize, exponent: i32) -> Dimension {
        let mut doubled_exponents: [i32; 7] = [0; 7];
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
        let mut doubled_exponents: [i32; 7] = [0; 7];
        for index in 0..7 {
            let doubled: f64 = self.doubled_exponents[index] as f64 * exponent;
            if doubled.is_nan() || (doubled - doubled.round_ties_even()).abs() > 1e-9 {
                return None;
            }
            doubled_exponents[index] = doubled.round_ties_even() as i32;
        }
        Some(Dimension { doubled_exponents })
    }

    fn combine(&self, other: &Dimension, combine: impl Fn(i32, i32) -> i32) -> Dimension {
        let mut doubled_exponents: [i32; 7] = [0; 7];
        for index in 0..7 {
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
        // Conventional order: kg m s A K mol cd
        for index in [1usize, 0, 2, 3, 4, 5, 6] {
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
    pub name: &'static str,
    pub to_si: f64,
    pub dimension: Dimension,
}

struct BaseDimensions {
    length: Dimension,
    mass: Dimension,
    time: Dimension,
    current: Dimension,
    temperature: Dimension,
    amount: Dimension,
    luminous: Dimension,
    force: Dimension,
    energy: Dimension,
}

fn base_dimensions() -> BaseDimensions {
    let length: Dimension = Dimension::base(0, 1);
    let mass: Dimension = Dimension::base(1, 1);
    let time: Dimension = Dimension::base(2, 1);
    let force: Dimension = mass.multiply(&length).divide(&time).divide(&time);
    BaseDimensions {
        length,
        mass,
        time,
        current: Dimension::base(3, 1),
        temperature: Dimension::base(4, 1),
        amount: Dimension::base(5, 1),
        luminous: Dimension::base(6, 1),
        force,
        energy: force.multiply(&length),
    }
}

/// Units known to calculator literals. A small built-in set until the full table arrives (phase 2).
pub static UNITS: LazyLock<HashMap<&'static str, UnitDefinition>> = LazyLock::new(|| {
    let base: BaseDimensions = base_dimensions();
    let definitions: Vec<(&'static str, f64, Dimension)> = vec![
        ("m", 1.0, base.length),
        ("km", 1000.0, base.length),
        ("cm", 0.01, base.length),
        ("mm", 0.001, base.length),
        ("um", 1e-6, base.length),
        ("nm", 1e-9, base.length),
        ("s", 1.0, base.time),
        ("ms", 0.001, base.time),
        ("us", 1e-6, base.time),
        ("ns", 1e-9, base.time),
        ("min", 60.0, base.time),
        ("h", 3600.0, base.time),
        ("kg", 1.0, base.mass),
        ("g", 0.001, base.mass),
        ("A", 1.0, base.current),
        ("K", 1.0, base.temperature),
        ("mol", 1.0, base.amount),
        ("cd", 1.0, base.luminous),
        ("lm", 1.0, base.luminous),
        ("lx", 1.0, base.luminous.divide(&base.length).divide(&base.length)),
        ("N", 1.0, base.force),
        ("J", 1.0, base.energy),
        ("W", 1.0, base.energy.divide(&base.time)),
        ("Hz", 1.0, Dimension::NONE.divide(&base.time)),
        ("rad", 1.0, Dimension::NONE),
        ("sr", 1.0, Dimension::NONE),
    ];
    definitions.into_iter().map(|(name, to_si, dimension)| (name, UnitDefinition { name, to_si, dimension })).collect()
});

/// Named SI units shown next to a result's dimension.
pub static DERIVED_NAMES: LazyLock<HashMap<Dimension, &'static str>> = LazyLock::new(|| {
    let base: BaseDimensions = base_dimensions();
    HashMap::from([
        (base.force, "N"),
        (base.energy, "J"),
        (base.energy.divide(&base.time), "W"),
        (base.luminous.divide(&base.length).divide(&base.length), "lx"),
        (Dimension::NONE.divide(&base.time), "Hz"),
    ])
});

pub fn is_unit_name(name: &str) -> bool {
    UNITS.contains_key(name)
}
