/**
 * Baseline security headers. `frame-ancestors` is the only CSP directive set:
 * it stops clickjacking without any risk of blocking a legitimate resource,
 * which a full `default-src` policy would need auditing the API and font
 * origins to get right. This is the back-office console holding customer PII
 * and payment data, so it denies every capability except the camera used by
 * the image upload inputs.
 */
const securityHeaders = [
  { key: "X-Content-Type-Options", value: "nosniff" },
  { key: "X-Frame-Options", value: "DENY" },
  { key: "Content-Security-Policy", value: "frame-ancestors 'none'" },
  { key: "Referrer-Policy", value: "strict-origin-when-cross-origin" },
  { key: "Permissions-Policy", value: "microphone=(), payment=(), usb=(), geolocation=(), camera=(self)" },
];

/** @type {import('next').NextConfig} */
const nextConfig = {
  async headers() {
    return [{ source: "/:path*", headers: securityHeaders }];
  },
  async redirects() {
    return [
      // The Glavyx Coins page lived at /nestly-coins until the rebrand reached
      // the route. Kept so bookmarks and anything linking the old path still land.
      { source: "/nestly-coins", destination: "/glavyx-coins", permanent: true },
    ];
  },
};

export default nextConfig;
