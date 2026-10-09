//! The HLSL reference: DXC compiles a line (with the code it uses) to DXIL, and Direct3D 12's WARP adapter runs it.

pub mod checker;
pub mod emitter;
pub mod warp_device;
