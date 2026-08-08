const allowedIllustrationAssetHosts = new Set([
  "assets-global.website-files.com",
  "cdn.prod.website-files.com",
  "humaaans.com",
  "openpeeps.com",
  "www.humaaans.com",
  "www.openpeeps.com"
]);

export function isAllowedIllustrationAssetUrl(value) {
  let url;
  try {
    url = value instanceof URL ? value : new URL(value);
  } catch {
    return false;
  }

  return url.protocol === "https:" &&
    url.username === "" &&
    url.password === "" &&
    (url.port === "" || url.port === "443") &&
    allowedIllustrationAssetHosts.has(url.hostname.toLowerCase());
}
