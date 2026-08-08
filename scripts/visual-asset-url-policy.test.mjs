import assert from "node:assert/strict";
import test from "node:test";

import { isAllowedIllustrationAssetUrl } from "./visual-asset-url-policy.mjs";

test("accepts the maintained illustration hosts over HTTPS", function() {
  const allowed = [
    "https://cdn.prod.website-files.com/library/figure.svg",
    "https://assets-global.website-files.com/library/figure.png?width=640",
    "https://www.openpeeps.com/assets/figure.svg",
    "https://www.humaaans.com/assets/figure.png"
  ];

  for (const url of allowed) {
    assert.equal(isAllowedIllustrationAssetUrl(url), true, url);
  }
});

test("rejects substring bypasses and unsafe URL forms", function() {
  const rejected = [
    "https://attacker.example/website-files.com/payload.svg",
    "https://assets-global.website-files.com.attacker.example/payload.svg",
    "https://attacker.example/openpeeps/payload.svg",
    "http://cdn.prod.website-files.com/payload.svg",
    "https://user@cdn.prod.website-files.com/payload.svg",
    "https://cdn.prod.website-files.com:8443/payload.svg",
    "not-a-url"
  ];

  for (const url of rejected) {
    assert.equal(isAllowedIllustrationAssetUrl(url), false, url);
  }
});
