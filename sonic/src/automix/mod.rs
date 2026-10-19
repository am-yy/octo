//! The transition analysis of the Octo apps' audio engine, copied in by
//! `sync-automix.sh` so a profile made here is the one a player would make.
//! Only this file and `parity_tests.rs` belong to octo-sonic; the others are
//! the engine's own and are changed there, then copied again.
//!
//! The copy carries what the players use as well, so unused parts are
//! allowed, and it keeps the engine's formatting.

#[rustfmt::skip]
#[allow(dead_code)]
pub mod dsp;
#[rustfmt::skip]
#[allow(dead_code)]
pub mod envelope;
#[rustfmt::skip]
#[allow(dead_code)]
pub mod features;
#[rustfmt::skip]
#[allow(dead_code)]
pub mod profile;
#[cfg(test)]
#[rustfmt::skip]
#[allow(dead_code)]
pub mod synthetic;

#[cfg(test)]
mod parity_tests;
