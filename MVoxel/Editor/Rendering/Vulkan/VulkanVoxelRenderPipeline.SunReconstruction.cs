namespace MVoxel.Editor.Rendering.Vulkan;

internal sealed unsafe partial class VulkanVoxelRenderPipeline
{
    private const string SunReconstructionShader = """
        uint Hash(uint value);
        bool TraceSunMapClosest(vec3 origin, vec3 direction, float maxDistance, out float closest);
        int SunRecord(ivec2 p) { return (p.y * int(values[11]) + p.x) * 12; }
        vec3 SunPosition(int i) { return vec3(sunDepth[i],sunDepth[i+1],sunDepth[i+2]); }
        vec3 SunNormal(int i) { return vec3(sunDepth[i+4],sunDepth[i+5],sunDepth[i+6]); }
        float SunFocal() { return values[86]>0.5 && values[87]>0.0 ? values[15]/(2.0*tan(radians(clamp(values[87],20.0,120.0))*0.5)) : max(values[3],1.0); }
        // 正交投射盒：相机位置不动，射线沿相机平面按每像素世界尺寸平移，方向平行。
        float SunWorldPerPixel() { return values[87]<0.0 ? -values[87]*2.0/max(values[15],1.0) : 0.0; }
        void BuildSunReconstruction(ivec2 pixel)
        {
            ivec2 size=ivec2(values[11],values[15]);
            if(any(greaterThanEqual(pixel,size))) return;
            int i=SunRecord(pixel);
            vec3 camera=vec3(values[0],values[1],values[2]);
            vec3 forward=vec3(values[4],values[5],values[6]);
            vec3 right=vec3(values[8],values[9],values[10]);
            vec3 up=vec3(values[12],values[13],values[14]);
            vec2 offset=vec2(pixel)+0.5-vec2(size)*0.5;
            vec3 dir;
            if(values[87]<0.0)
            {
                float wpp=SunWorldPerPixel();
                camera+=right*(offset.x*wpp)-up*(offset.y*wpp);
                dir=normalize(forward);
            }
            else dir=normalize(forward+(right*offset.x-up*offset.y)/SunFocal());
            float distance; vec3 normal; vec4 color; uint flags,slot; ivec3 cell,extent;
            bool hit=TraceClosest(camera,dir,distance,normal,color,flags,slot,cell,extent,false);
            float ground=(values[35]>0.5 || (values[86]<0.5 && values[90]>0.5 && values[115]<0.5)) && abs(dir.y)>0.00001
                ? -camera.y/dir.y : 3.402823e+38;
            float water=values[144]>0.5 && abs(dir.y)>0.00001
                ? (values[145]-camera.y)/dir.y : 3.402823e+38;
            if(ground<=0.001) ground=3.402823e+38;
            if(water<=0.001) water=3.402823e+38;
            sunDepth[i+3]=-1.0; sunDepth[i+7]=1.0; sunDepth[i+8]=0.0; sunDepth[i+9]=1.0;
            // 水面的波浪法线随位置变化，主渲染会另行计算；没有一致的导引时回退射线。
            if(water<=ground && (!hit || water<distance) && water<values[97]) return;
            if(ground<water && (!hit || ground<distance) && ground<values[97])
            { hit=true; distance=ground; normal=vec3(0,1,0); }
            if(!hit || distance<values[96] || distance>values[97]) return;
            vec3 point=camera+dir*distance;
            sunDepth[i]=point.x; sunDepth[i+1]=point.y; sunDepth[i+2]=point.z;
            sunDepth[i+3]=max(distance*dot(dir,forward),0.0001);
            sunDepth[i+4]=normal.x; sunDepth[i+5]=normal.y; sunDepth[i+6]=normal.z;
            vec3 light=normalize(vec3(values[24],values[25],values[26]));
            if(dot(normal,light)<=0.0) return;
            vec3 tangent,bitangent; SunDiskBasis(light,tangent,bitangent);
            uint state=Hash(uint(pixel.x)*1973u+uint(pixel.y)*9277u+17u);
            int count=clamp(int(values[32])/2,2,8);
            float visible=0.0,blockers=0.0,blockerSum=0.0;
            float cosMax=inversesqrt(1.0+values[31]*values[31]);
            float rotation=6.28318530718*Random(state);
            for(int n=0;n<8;n++)
            {
                if(n>=count) break;
                // 分层半径与黄金角避免少量样本在太阳盘上随机扎堆。
                float c=mix(1.0,cosMax,(float(n)+0.5)/float(count));
                float angle=rotation+float(n)*2.39996323;
                vec3 ray=light*c+sqrt(max(0.0,1.0-c*c))*(tangent*cos(angle)+bitangent*sin(angle));
                float d;
                bool blocked=TraceSunMapClosest(point+normal*0.002,ray,10000.0,d);
                if(blocked) { blockerSum+=d; blockers+=1.0; } else visible+=1.0;
            }
            sunDepth[i+7]=visible/float(count);
            sunDepth[i+8]=blockers>0.0 ? blockerSum/blockers : 0.0;
        }
        void FilterSunReconstruction(ivec2 pixel)
        {
            ivec2 size=ivec2(values[11],values[15]);
            if(any(greaterThanEqual(pixel,size))) return;
            int i=SunRecord(pixel);
            if(sunDepth[i+3]<=0.0) return;
            vec3 point=SunPosition(i),normal=SunNormal(i);
            float footprint=values[87]<0.0 ? max(SunWorldPerPixel(),0.0001)
                : max(sunDepth[i+3]/SunFocal(),0.0001);
            float blocker=sunDepth[i+8];
            for(int y=-2;y<=2;y++) for(int x=-2;x<=2;x++)
            {
                int j=SunRecord(clamp(pixel+ivec2(x,y),ivec2(0),size-1));
                if(sunDepth[j+3]>0.0 && dot(normal,SunNormal(j))>0.95 && abs(dot(SunPosition(j)-point,normal))<footprint*1.5)
                    blocker=max(blocker,sunDepth[j+8]);
            }
            float radius=clamp(blocker*values[31]/footprint,0.0,32.0);
            if(radius<0.5) { sunDepth[i+9]=sunDepth[i+7]; return; }
            float sum=0.0,total=0.0;
            for(int y=-3;y<=3;y++) for(int x=-3;x<=3;x++)
            {
                vec2 disk=vec2(x,y)/3.0;
                if(dot(disk,disk)>1.0) continue;
                int j=SunRecord(clamp(pixel+ivec2(round(disk*radius)),ivec2(0),size-1));
                if(sunDepth[j+3]<=0.0 || dot(normal,SunNormal(j))<0.95) continue;
                vec3 delta=SunPosition(j)-point;
                float plane=abs(dot(delta,normal));
                if(plane>max(0.01,footprint*0.75) || length(delta)>footprint*(radius*3.0+2.0)) continue;
                float w=exp(-2.0*dot(disk,disk)-plane/max(footprint*0.2,0.001));
                sum+=sunDepth[j+7]*w; total+=w;
            }
            sunDepth[i+9]=total>0.0 ? sum/total : sunDepth[i+7];
        }
        float ReconstructedSun(vec3 point, vec3 normal)
        {
            vec3 delta=point-vec3(values[0],values[1],values[2]);
            float z=dot(delta,vec3(values[4],values[5],values[6]));
            if(z<=0.0) return -1.0;
            vec2 size=vec2(values[11],values[15]);
            vec2 p;
            float footprint;
            if(values[87]<0.0)
            {
                // 正交反投影不带 1/z 透视除法；一个像素恒对应 wpp 个世界单位。
                float wpp=SunWorldPerPixel();
                p=vec2(dot(delta,vec3(values[8],values[9],values[10])),-dot(delta,vec3(values[12],values[13],values[14])))/wpp+size*0.5;
                footprint=wpp;
            }
            else
            {
                p=vec2(dot(delta,vec3(values[8],values[9],values[10])),-dot(delta,vec3(values[12],values[13],values[14])))*SunFocal()/z+size*0.5;
                footprint=z/SunFocal();
            }
            if(any(lessThan(p,vec2(0))) || any(greaterThanEqual(p,size))) return -1.0;
            int i=SunRecord(ivec2(p));
            if(sunDepth[i+3]<=0.0 || length(SunPosition(i)-point)>max(0.01,footprint*1.5)) return -1.0;
            vec3 guideNormal = SunNormal(i);
            if(dot(guideNormal,normal)<0.98 ||
                abs(dot(SunPosition(i)-point,guideNormal))>max(0.005,footprint*0.25)) return -1.0;
            return sunDepth[i+9];
        }
        """;
}
