// Update signing for ShaderCalc's self-update (app/src-tauri/src/update.rs). Node only, no packages.
//
//   node scripts/sign-release.mjs keygen <public-key-file>
//     Makes an ed25519 key pair: writes the public key (base64 of the raw 32 bytes) to the file the app builds in,
//     prints the private key (PKCS#8 PEM) on stdout for the SHADERCALC_SIGNING_KEY secret.
//
//   node scripts/sign-release.mjs manifest <version> <exe> <out-dir> [notes-file]
//     Writes <out-dir>/update.json (version, notes, exe name, size, SHA-256) and update.json.sig, the base64
//     signature of update.json's bytes, with the private key from SHADERCALC_SIGNING_KEY.
import { createHash, createPrivateKey, generateKeyPairSync, sign } from "node:crypto";
import { mkdirSync, readFileSync, statSync, writeFileSync } from "node:fs";
import { basename, join } from "node:path";

const [command, ...rest] = process.argv.slice(2);

if (command === "keygen") {
  const [publicKeyFile] = rest;
  const { publicKey, privateKey } = generateKeyPairSync("ed25519");
  // SPKI DER of an ed25519 key ends with the raw 32-byte key
  const raw = publicKey.export({ format: "der", type: "spki" }).subarray(-32);
  writeFileSync(publicKeyFile, raw.toString("base64"));
  process.stdout.write(privateKey.export({ format: "pem", type: "pkcs8" }));
} else if (command === "manifest") {
  const [version, exe, outDirectory, notesFile] = rest;
  const keyText = process.env.SHADERCALC_SIGNING_KEY;
  if (!keyText) {
    throw new Error("SHADERCALC_SIGNING_KEY is not set");
  }
  const bytes = readFileSync(exe);
  const manifest = {
    version,
    notes: notesFile ? readFileSync(notesFile, "utf8").trim() : "",
    exe: { name: basename(exe), size: statSync(exe).size, sha256: createHash("sha256").update(bytes).digest("hex") },
  };
  const manifestBytes = Buffer.from(JSON.stringify(manifest, null, 2));
  mkdirSync(outDirectory, { recursive: true });
  writeFileSync(join(outDirectory, "update.json"), manifestBytes);
  const signature = sign(null, manifestBytes, createPrivateKey(keyText));
  writeFileSync(join(outDirectory, "update.json.sig"), signature.toString("base64"));
  console.log(`update.json for ${version}: ${manifest.exe.name}, ${manifest.exe.size} bytes, ${manifest.exe.sha256}`);
} else {
  console.error("usage: sign-release.mjs keygen <public-key-file> | manifest <version> <exe> <out-dir> [notes-file]");
  process.exit(1);
}
