//! HLSL interpreter with DXC + WARP as its bit-exact reference.

// Symbols hash by their id only, so their interior mutability (initializer, constant value) can't move a key
#![allow(clippy::mutable_key_type)]
// Component loops index several parallel arrays (bits, units, kinds), as the formulas they follow do
#![allow(clippy::needless_range_loop)]

pub mod binding;
pub mod diagnostics;
pub mod docs;
pub mod evaluation;
pub mod exports;
pub mod reference;
pub mod semantics;
pub mod session;
pub mod syntax;
pub mod trace;
pub mod types;
pub mod units;
pub mod values;
pub mod worksheet;
