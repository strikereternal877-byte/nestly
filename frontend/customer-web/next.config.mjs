/**
 * Baseline security headers. `frame-ancestors` is the only CSP directive set:
 * it stops clickjacking without any risk of blocking a legitimate resource,
 * which a full `default-src` policy would need auditing the API, Firebase and
 * font origins to get right. Permissions-Policy allows only what this app
 * actually uses - geolocation (src/lib/location.ts) - and denies the rest.
 */
const securityHeaders = [
  { key: "X-Content-Type-Options", value: "nosniff" },
  { key: "X-Frame-Options", value: "DENY" },
  { key: "Content-Security-Policy", value: "frame-ancestors 'none'" },
  { key: "Referrer-Policy", value: "strict-origin-when-cross-origin" },
  { key: "Permissions-Policy", value: "camera=(), microphone=(), payment=(), usb=(), geolocation=(self)" },
];

/** @type {import('next').NextConfig} */
const nextConfig = {
  async headers() {
    return [{ source: "/:path*", headers: securityHeaders }];
  },
};

export default nextConfig;
