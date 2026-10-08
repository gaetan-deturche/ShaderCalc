---
title: KalkGui supplement
---

## Bitwise Operators

### Bitwise AND and OR
<!-- keywords: &, |, and, or, bitwise -->

`&` and `|` combine the bits of two **integers**. They don't apply to floats (`1.5 & 1` is an error) or to vectors. On arrays, `&` and `|` are set intersection and union instead.

```kalk
>>> 0b1100 & 0b1010
# 12 & 10
out = 8
>>> 0b1100 | 0b1010
# 12 | 10
out = 14
>>> 5 & 1
# 5 & 1
out = 1
```

### Bit shifts
<!-- keywords: <<, >>, shift -->

`<<` shifts left and `>>` shifts right. `>>` is arithmetic on signed integers (the sign is kept) and logical on unsigned ones. Shifts also apply per component to integer vectors, and a result that no longer fits an `int` becomes a `long`.

```kalk
>>> 1 << 4
# 1 << 4
out = 16
>>> 256 >> 2
# 256 >> 2
out = 64
>>> -16 >> 2
# -16 >> 2
out = -4
>>> uint(4294967295) >> 28
# uint(4294967295) >> 28
out = 15
>>> int4(1, 2, 3, 4) << 2
# int4(1, 2, 3, 4) << 2
out = int4(4, 8, 12, 16)
>>> 1 << 40
# 1 << 40
out = 1_099_511_627_776
```

### XOR and NOT
<!-- keywords: xor, not, ~, ^^, complement -->

kalk has no xor operator (`^` is the power operator) and no `~` complement. These identities give the same results, negative numbers included (two's complement):

```kalk
>>> a = 12; b = 10
# a = 12; b = 10
a = 12
b = 10
>>> (a | b) - (a & b)
# (a | b) - (a & b)
out = 6
>>> -a - 1
# -a - 1
out = -13
```

`^^` is not xor either: kalk reads `a ^^ b` as a product.

```kalk
>>> 12 ^^ 10
# 12 * ^^ 10
out = 120
```

## Assignment Operators

### Compound assignment and increments
<!-- keywords: +=, -=, *=, /=, ++, --, increment, decrement -->

`+=`, `-=`, `*=` and `/=` update a variable in place. `x++` and `x--` return the old value, then update the variable. `<<=` and the other bitwise compound assignments are not supported.

```kalk
>>> x = 5
# x = 5
x = 5
>>> x += 2
# x += 2
x = 7
>>> x *= 3
# x *= 3
x = 21
>>> x++
# x++
x = 22
out = 21
>>> x
# x
out = 22
```

## Bit Functions

### Bit counts and reinterpret casts
<!-- keywords: countbits, firstbithigh, firstbitlow, asuint, asint, asfloat, bitcast, popcount, popcnt, lzcnt, tzcnt, clz, ctz -->

kalk ships HLSL-style bit functions: `countbits` (population count), `firstbithigh`, `firstbitlow`, and the reinterpret casts `asint`, `asuint`, `asfloat`, `asdouble`, `aslong`, `asulong` and `bitcast`.

Unlike HLSL, kalk's `firstbithigh` counts from the top bit: `firstbithigh(0b1011)` is 28, the number of leading zeros, where HLSL returns the bit index 3.

```kalk
>>> countbits(0b1011)
# countbits(11)
out = 3
>>> firstbithigh(0b1011)
# firstbithigh(11)
out = 28
>>> firstbitlow(0b1000)
# firstbitlow(8)
out = 3
>>> asuint(1.0f)
# asuint(1.0f)
out = 1_065_353_216
>>> asfloat(0x3F800000)
# asfloat(1065353216)
out = 1
```

`bin` and `hex` show the bytes of a value, and `display dev` shows the full bit layout of every result.

## Other Operators

### Logical NOT and history recall
<!-- keywords: !, not, history -->

`!` negates a boolean. Directly in front of a number it is kalk's history recall instead: `!5` re-runs entry 5 of `history`.

```kalk
>>> !true
# !true
out = false
>>> !(1 > 2)
# !(1 > 2)
out = true
```
