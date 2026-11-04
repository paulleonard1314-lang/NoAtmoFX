# Cirrus texture (1024^2, tileable): streaks made by LIC along a meandering wind field.
# Only used when Weather > OwnCirrus is on (off by default, the game's own cirrus is used instead).
# Writes NOClouds_Cirrus.bytes (raw RGBA8) for unity/Assets/NoAtmoFX.
import numpy as np
from scipy.ndimage import map_coordinates, gaussian_filter
from PIL import Image
N=1024; rng=np.random.default_rng(7)
def pnoise(scale_px, seed, aniso=1.0):
    # tileable spectral noise (FFT): feature size scale_px, aniso stretches along x
    r=np.random.default_rng(seed)
    kx=np.fft.fftfreq(N)[None,:]*N; ky=np.fft.fftfreq(N)[:,None]*N
    k=np.sqrt((kx/aniso)**2+ky**2); k0=N/scale_px
    amp=np.exp(-(k/k0)**2)*(k>0)
    ph=np.exp(2j*np.pi*r.random((N,N)))
    f=np.real(np.fft.ifft2(amp*ph)); f=(f-f.mean())/f.std()
    return f
def lic(src, vx, vy, L, step=1.0, sides=(1,-1), decay=2):
    acc=np.zeros_like(src); w=0
    for sgn in sides:
        px=np.tile(np.arange(N,dtype=np.float32),(N,1)); py=px.T.copy()
        for i in range(L):
            ux=map_coordinates(vx,[py,px],order=1,mode='grid-wrap'); uy=map_coordinates(vy,[py,px],order=1,mode='grid-wrap')
            px=px+sgn*ux*step; py=py+sgn*uy*step
            wt=1-(i/L)**decay
            acc+=wt*map_coordinates(src,[py,px],order=1,mode='grid-wrap'); w+=wt
    return acc/w
def field(seed, meander, curl_px):
    # wind along +x with meander (curl of a noise field = divergence free)
    psi=pnoise(curl_px, seed)*meander
    vy=-(np.roll(psi,-1,1)-np.roll(psi,1,1))*0.5*curl_px/6
    vx=1.0+(np.roll(psi,-1,0)-np.roll(psi,1,0))*0.5*curl_px/6
    m=np.sqrt(vx**2+vy**2); return vx/m, vy/m
out=[]
for ch,(seed,mea,cpx) in enumerate([(1,0.35,180),(2,0.55,120)]):
    vx,vy=field(seed,mea,cpx)
    # streaks: fine + medium, moderate contrast
    f1=lic(gaussian_filter(rng.random((N,N)),0.7,mode='wrap'),vx,vy,110,1.0)
    f2=lic(gaussian_filter(rng.random((N,N)),2.0,mode='wrap'),vx,vy,70,1.5)
    f1=(f1-f1.mean())/f1.std(); f2=(f2-f2.mean())/f2.std()
    fib=np.clip(0.55+0.18*f1+0.22*f2,0,1)
    # sparse "heads" with tails downwind (uncinus)
    hd=np.clip(pnoise(28,seed+30)+0.4*pnoise(9,seed+31)-1.3,0,None)
    tail=lic(hd,vx,vy,160,1.5,sides=(1,),decay=1)
    tail=tail/np.percentile(tail,99.5)
    # elongated patches (veil)
    env=lic(pnoise(90,seed+10)+0.6*pnoise(40,seed+20),vx,vy,40,2.0)
    env=(env-env.mean())/env.std()
    out.append((fib,np.clip(tail,0,1),env))
R=out[0][0]; G=np.clip(out[0][1]*0.7+out[1][1]*0.6,0,1)
B=np.clip(0.5+0.25*out[0][2],0,1); A=out[1][0]
img=np.stack([R,G,B,A],-1)
np.save('ci.npy',img)
(img*255).astype(np.uint8).tofile('NOClouds_Cirrus.bytes')
Image.fromarray((img[...,:3]*255).astype(np.uint8)).save('ci_rgb.png')
cov=0.5
e=np.clip(np.maximum((B-0.66+cov*0.25)/0.2, G*1.3),0,1)
a=e*R**1.5*1.3
Image.fromarray((np.clip(a,0,1)*255).astype(np.uint8)).save('ci_prev.png')
print('ok')
