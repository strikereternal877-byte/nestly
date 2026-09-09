/**
 * Baseline security headers. `frame-ancestors` is the only CSP directive set:
 * it stops clickjacking without any risk of blocking a legitimate resource,
 * which a full `default-src` policy would need auditing the API, Firebase,
 * SignalR hub and font origins to get right. Permissions-Policy keeps the two
 * capabilities this app genuinely needs - the camera for completion-proof
 * photos and geolocation for live job tracking - and denies the rest.
 */
const securityHeaders = [
  { key: "X-Content-Type-Options", value: "nosniff" },
  { key: "X-Frame-Options", value: "DENY" },
  { key: "Content-Security-Policy", value: "frame-ancestors 'none'" },
  { key: "Referrer-Policy", value: "strict-origin-when-cross-origin" },
  { key: "Permissions-Policy", value: "microphone=(), payment=(), usb=(), camera=(self), geolocation=(self)" },
];

/** @type {import('next').NextConfig} */
const nextConfig = {
  async headers() {
    return [{ source: "/:path*", headers: securityHeaders }];
  },
};

export default nextConfig;
