/** @type {import('next').NextConfig} */
const nextConfig = {
  output: 'export',
  basePath: '/FlyShelf',
  assetPrefix: '/FlyShelf/',
  images: {
    unoptimized: true,
  },
  trailingSlash: true,
};

export default nextConfig;
