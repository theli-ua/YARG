// Port of https://www.shadertoy.com/view/4t3BWl#

Shader "InsideTheMatrix"
{
    SubShader
    {
        Pass
        {
            ColorMask RGB

            // We don't want this to be culled
            Cull Off

            ZWrite On
            ZTest Off
            
            CGPROGRAM
// Upgrade NOTE: excluded shader from OpenGL ES 2.0 because it uses non-square matrices
#pragma exclude_renderers gles
            #pragma vertex vert
            #pragma fragment frag
                
                
            #include "UnityCG.cginc"
            #include "ShaderToy.cginc"
            


            // Constants
            static const int ITERATIONS = 40;
            static const float STRIP_CHARS_MIN = 7.0;
            static const float STRIP_CHARS_MAX = 40.0;
            static const float STRIP_CHAR_HEIGHT = 0.15;
            static const float STRIP_CHAR_WIDTH = 0.10;
            static const float ZCELL_SIZE = 1.0 * (STRIP_CHAR_HEIGHT * STRIP_CHARS_MAX);
            static const float XYCELL_SIZE = 12.0 * STRIP_CHAR_WIDTH;
            
            static const int BLOCK_SIZE = 10;
            static const int BLOCK_GAP = 2;
            
            static const float WALK_SPEED = 1.0 * XYCELL_SIZE;
            static const float BLOCKS_BEFORE_TURN = 3.0;
            
            static const float PI = 3.14159265359;
            
            // Random functions
            float hash(float v) {
                return frac(sin(v) * 43758.5453123);
            }
            
            float hash(float2 v) {
                return hash(dot(v, float2(5.3983, 5.4427)));
            }
            
            float2 hash2(float2 v) {
                v = mul(v, float2x2(127.1, 311.7, 269.5, 183.3));
                return frac(sin(v) * 43758.5453123);
            }
            
            float4 hash4(float2 v) {
                float4 p = float4(v.x * 127.1, v.y * 311.7,
                                 v.x * 269.5, v.y * 183.3);
                p += float4(v.x * 113.5, v.y * 271.9,
                           v.x * 246.1, v.y * 124.6);
                return frac(sin(p) * 43758.5453123);
            }
            
            float4 hash4(float3 v) {
                float4 p = float4(
                    v.x * 127.1 + v.y * 311.7 + v.z * 74.7,
                    v.x * 269.5 + v.y * 183.3 + v.z * 246.1,
                    v.x * 113.5 + v.y * 271.9 + v.z * 124.6,
                    v.x * 271.9 + v.y * 269.5 + v.z * 311.7
                );
                return frac(sin(p) * 43758.5453123);
            }
            
            // Rune symbols
            float rune_line(float2 p, float2 a, float2 b) {
                p -= a;
                b -= a;
                float h = clamp(dot(p, b) / dot(b, b), 0.0, 1.0);
                return length(p - b * h);
            }
            
            float rune(float2 U, float2 seed, float highlight) {
                float d = 1e5;
                for (int i = 0; i < 4; i++) {
                    float4 pos = hash4(seed);
                    seed += 1.0;
                    
                    // Each rune touches the edge of its box on all 4 sides
                    if (i == 0) pos.y = 0.0;
                    if (i == 1) pos.x = 0.999;
                    if (i == 2) pos.x = 0.0;
                    if (i == 3) pos.y = 0.999;
                    
                    // Snap the random line endpoints to a grid 2x3
                    float4 snaps = float4(2, 3, 2, 3);
                    pos = (floor(pos * snaps) + 0.5) / snaps;
                    
                    if (pos.x != pos.z || pos.y != pos.w) // Filter out single points
                        d = min(d, rune_line(U, pos.xy, pos.zw + 0.001));
                }
                return smoothstep(0.1, 0.0, d) + highlight * smoothstep(0.4, 0.0, d);
            }
            
            float random_char(float2 outer, float2 inner, float highlight) {
                vec2 seed = vec2(dot(outer, vec2(269.5, 183.3)), dot(outer, vec2(113.5, 271.9))) / 43758.5453; 
                // float2 seed = float2(dot(outer, float2(269.5, 183.3)), dot(outer, float2(113.5, 271.9)));
                return rune(inner, seed, highlight);
            }
            
            // Digital rain effect
            float3 rain(float3 ro3, float3 rd3, float time) {
                float4 result = float4(0.0, 0.0, 0.0, 0.0);
                
                // Normalized 2d projection
                float2 ro2 = float2(ro3.xy);
                float2 rd2 = normalize(float2(rd3.xy));
                
                // Use formulas `ro3 + rd3 * t3` and `ro2 + rd2 * t2`, `t3_to_t2` is a multiplier to convert t3 to t2
                bool prefer_dx = abs(rd2.x) > abs(rd2.y);
                float t3_to_t2 = prefer_dx ? rd3.x / rd2.x : rd3.y / rd2.y;
                
                // Horizontal space (xy) is divided into cells (columns in 3D)
                // Each xy-cell is divided into vertical cells (along z) - each contains one raindrop
                
                int3 cell_side = int3(step(0.0, rd3));
                int3 cell_shift = int3(sign(rd3));
                
                // Move through xy-cells in ray direction
                float t2 = 0.0;
                int2 next_cell = int2(floor(ro2 / XYCELL_SIZE));
                
                for (int i = 0; i < ITERATIONS; i++) {
                    int2 cell = next_cell;
                    float t2s = t2;
                    
                    // Find intersection with nearest side of current xy-cell
                    float2 side = float2(next_cell + cell_side.xy) * XYCELL_SIZE;
                    float2 t2_side = (side - ro2) / rd2;
                    
                    if (t2_side.x < t2_side.y) {
                        t2 = t2_side.x;
                        next_cell.x += cell_shift.x;
                    } else {
                        t2 = t2_side.y;
                        next_cell.y += cell_shift.y;
                    }
                    
                    // Gap cells
                    float2 cell_in_block = frac(float2(cell) / float(BLOCK_SIZE));
                    float gap = float(BLOCK_GAP) / float(BLOCK_SIZE);
                    if (cell_in_block.x < gap || cell_in_block.y < gap || 
                        (cell_in_block.x < (gap + 0.1) && cell_in_block.y < (gap + 0.1))) {
                        continue;
                    }
                    
                    // Return to 3d - start and end points of ray segment inside column
                    float t3s = t2s / t3_to_t2;
                    
                    // Move through z-cells of current column in ray direction
                    float pos_z = ro3.z + rd3.z * t3s;
                    float xycell_hash = hash(float2(cell));
                    float z_shift = xycell_hash * 11.0 - time * (0.5 + xycell_hash * 1.0 + 
                                                               xycell_hash * xycell_hash * 1.0 + 
                                                               pow(xycell_hash, 16.0) * 3.0);
                    float char_z_shift = floor(z_shift / STRIP_CHAR_HEIGHT);
                    z_shift = char_z_shift * STRIP_CHAR_HEIGHT;
                    int zcell = int(floor((pos_z - z_shift) / ZCELL_SIZE));
                    
                    for (int j = 0; j < 2; j++) {
                        // Calculate coordinates of the target (raindrop)
                        float4 cell_hash = hash4(float3(cell.x, cell.y, zcell));
                        float4 cell_hash2 = frac(cell_hash * float4(127.1, 311.7, 271.9, 124.6));
                        
                        float chars_count = cell_hash.w * (STRIP_CHARS_MAX - STRIP_CHARS_MIN) + STRIP_CHARS_MIN;
                        float target_length = chars_count * STRIP_CHAR_HEIGHT;
                        float target_rad = STRIP_CHAR_WIDTH / 2.0;
                        float target_z = (float(zcell) * ZCELL_SIZE + z_shift) + 
                                        cell_hash.z * (ZCELL_SIZE - target_length);
                        float2 target = float2(cell) * XYCELL_SIZE + target_rad + 
                                       cell_hash.xy * (XYCELL_SIZE - target_rad * 2.0);
                        
                        // Calculate distance between line segment and cell target
                        float2 s = target - ro2;
                        float tmin = dot(s, rd2);
                        
                        if (tmin >= t2s && tmin <= t2) {
                            float u = s.x * rd2.y - s.y * rd2.x;  // Horizontal coord in matrix strip
                            
                            if (abs(u) < target_rad) {
                                u = (u / target_rad + 1.0) / 2.0;
                                float z = ro3.z + rd3.z * tmin / t3_to_t2;
                                float v = (z - target_z) / target_length;  // Vertical coord in matrix strip
                                
                                if (v >= 0.0 && v < 1.0) {
                                    float c = floor(v * chars_count);
                                    float q = frac(v * chars_count);
                                    float2 char_hash = hash2(float2(c + char_z_shift, cell_hash2.x));
                                    
                                    if (char_hash.x >= 0.1 || c == 0.0) {
                                        float time_factor = floor(c == 0.0 ? time * 5.0 :
                                                              time * (1.0 * cell_hash2.z +
                                                                    cell_hash2.w * cell_hash2.w * 4.0 * 
                                                                    pow(char_hash.y, 4.0)));
                                        
                                        float a = random_char(float2(char_hash.x, time_factor), 
                                                           float2(u, q), 
                                                           max(1.0, 3.0 - c / 2.0) * 0.2);
                                        a *= clamp((chars_count - 0.5 - c) / 2.0, 0.0, 1.0);  // Tail fade
                                        
                                        if (a > 0.0) {
                                            float attenuation = 1.0 + pow(0.06 * tmin / t3_to_t2, 2.0);
                                            float3 col = (c == 0.0 ? 
                                                     float3(0.67, 1.0, 0.82) : 
                                                     float3(0.25, 0.80, 0.40)) / attenuation;
                                            
                                            float a1 = result.a;
                                            result.a = a1 + (1.0 - a1) * a;
                                            result.xyz = (result.xyz * a1 + col * (1.0 - a1) * a) / result.a;
                                            
                                            if (result.a > 0.98)
                                                return result.xyz;
                                        }
                                    }
                                }
                            }
                        }
                        // Go to next vertical cell
                        zcell += cell_shift.z;
                    }
                    // Go to next horizontal cell
                }
                
                return result.xyz * result.a;
            }
            
            // Rotation functions
            float2 rotate(float2 v, float a) {
                float s = sin(a);
                float c = cos(a);
                float2x2 m = float2x2(c, -s, s, c);
                return mul(m, v);
            }
            
            float3 rotateX(float3 v, float a) {
                float s = sin(a);
                float c = cos(a);
                return float3(v.x, c * v.y - s * v.z, s * v.y + c * v.z);
            }
            
            float3 rotateY(float3 v, float a) {
                float s = sin(a);
                float c = cos(a);
                return float3(c * v.x + s * v.z, v.y, -s * v.x + c * v.z);
            }
            
            float3 rotateZ(float3 v, float a) {
                float s = sin(a);
                float c = cos(a);
                return float3(c * v.x - s * v.y, s * v.x + c * v.y, v.z);
            }
            
            float smoothstep1(float x) {
                return smoothstep(0.0, 1.0, x);
            }
            
            fixed4 frag (v2f _iParam) : SV_Target
            {
                if (STRIP_CHAR_WIDTH > XYCELL_SIZE || STRIP_CHAR_HEIGHT * STRIP_CHARS_MAX > ZCELL_SIZE) {
                    // Error condition
                    return fixed4(1.0, 0.0, 0.0, 1.0);
                }
                
                // float2 uv = (i.uv * 2.0 - 1.0) * _ScreenParams.y / _ScreenParams.x;
                // uv.y *= _ScreenParams.x / _ScreenParams.y; // Correct aspect ratio
                float2 fragCoord = gl_FragCoord;
                vec2 uv = (fragCoord.xy * 2. - iResolution.xy) / iResolution.y;                

                float time = _Time.y * 1.;
                
                const float turn_rad = 0.25 / BLOCKS_BEFORE_TURN;
                const float turn_abs_time = (PI / 2.0 * turn_rad) * 1.5;
                const float turn_time = turn_abs_time / (1.0 - 2.0 * turn_rad + turn_abs_time);
                
                float level1_size = float(BLOCK_SIZE) * BLOCKS_BEFORE_TURN * XYCELL_SIZE;
                float level2_size = 4.0 * level1_size;
                float gap_size = float(BLOCK_GAP) * XYCELL_SIZE;
                
                float3 ro = float3(gap_size / 2.0, gap_size / 2.0, 0.0);
                float3 rd = float3(uv.x, 2.0, uv.y);
                
                float tq = frac(time / (level2_size * 4.0) * WALK_SPEED);
                float t8 = frac(tq * 4.0);
                float t1 = frac(t8 * 8.0);
                
                float2 prev;
                float2 dir;
                
                if (tq < 0.25) {
                    prev = float2(0.0, 0.0);
                    dir = float2(0.0, 1.0);
                } else if (tq < 0.5) {
                    prev = float2(0.0, 1.0);
                    dir = float2(1.0, 0.0);
                } else if (tq < 0.75) {
                    prev = float2(1.0, 1.0);
                    dir = float2(0.0, -1.0);
                } else {
                    prev = float2(1.0, 0.0);
                    dir = float2(-1.0, 0.0);
                }
                
                float angle = floor(tq * 4.0);
                prev *= 4.0;
                
                const float first_turn_look_angle = 0.4;
                const float second_turn_drift_angle = 0.5;
                const float fifth_turn_drift_angle = 0.25;
                
                float2 turn;
                float turn_sign = 0.0;
                float2 dirL = rotate(dir, -PI / 2.0);
                float2 dirR = -dirL;
                float up_down = 0.0;
                float rotate_on_turns = 1.0;
                float roll_on_turns = 1.0;
                float add_angel = 0.0;
                
                if (t8 < 0.125) {
                    turn = dirL;
                    turn_sign = -1.0;
                    angle -= first_turn_look_angle * (max(0.0, t1 - (1.0 - turn_time * 2.0)) / turn_time - 
                                                    max(0.0, t1 - (1.0 - turn_time)) / turn_time * 2.5);
                    roll_on_turns = 0.0;
                } else if (t8 < 0.250) {
                    prev += dir;
                    turn = dir;
                    dir = dirL;
                    angle -= 1.0;
                    turn_sign = 1.0;
                    add_angel += first_turn_look_angle * 0.5 + 
                                (-first_turn_look_angle * 0.5 + 1.0 + second_turn_drift_angle) * t1;
                    rotate_on_turns = 0.0;
                    roll_on_turns = 0.0;
                } else if (t8 < 0.375) {
                    prev += dir + dirL;
                    turn = dirR;
                    turn_sign = 1.0;
                    add_angel += second_turn_drift_angle * sqrt(1.0 - t1);
                } else if (t8 < 0.5) {
                    prev += dir + dir + dirL;
                    turn = dirR;
                    dir = dirR;
                    angle += 1.0;
                    turn_sign = 0.0;
                    up_down = sin(t1 * PI) * 0.37;
                } else if (t8 < 0.625) {
                    prev += dir + dir;
                    turn = dir;
                    dir = dirR;
                    angle += 1.0;
                    turn_sign = -1.0;
                    up_down = sin(-min(1.0, t1 / (1.0 - turn_time)) * PI) * 0.37;
                } else if (t8 < 0.750) {
                    prev += dir + dir + dirR;
                    turn = dirL;
                    turn_sign = -1.0;
                    add_angel -= (fifth_turn_drift_angle + 1.0) * smoothstep1(t1);
                    rotate_on_turns = 0.0;
                    roll_on_turns = 0.0;
                } else if (t8 < 0.875) {
                    prev += dir + dir + dir + dirR;
                    turn = dir;
                    dir = dirL;
                    angle -= 1.0;
                    turn_sign = 1.0;
                    add_angel -= fifth_turn_drift_angle - smoothstep1(t1) * (fifth_turn_drift_angle * 2.0 + 1.0);
                    rotate_on_turns = 0.0;
                    roll_on_turns = 0.0;
                } else {
                    prev += dir + dir + dir;
                    turn = dirR;
                    turn_sign = 1.0;
                    angle += fifth_turn_drift_angle * (1.5 * min(1.0, (1.0 - t1) / turn_time) - 
                                                     0.5 * smoothstep1(1.0 - min(1.0, t1 / (1.0 - turn_time))));
                }
                
                // Mouse input is removed for Unity version
                // We'll use regular camera movement instead
                angle += add_angel;
                
                rd = rotateX(rd, up_down);
                
                float2 p;
                if (turn_sign == 0.0) {
                    // Move forward
                    p = prev + dir * (turn_rad + 1.0 * t1);
                }
                else if (t1 > (1.0 - turn_time)) {
                    // Turn
                    float tr = (t1 - (1.0 - turn_time)) / turn_time;
                    float2 c = prev + dir * (1.0 - turn_rad) + turn * turn_rad;
                    p = c + turn_rad * rotate(dir, (tr - 1.0) * turn_sign * PI / 2.0);
                    angle += tr * turn_sign * rotate_on_turns;
                    rd = rotateY(rd, sin(tr * turn_sign * PI) * 0.2 * roll_on_turns);  // Roll
                } else {
                    // Move forward
                    t1 /= (1.0 - turn_time);
                    p = prev + dir * (turn_rad + (1.0 - turn_rad * 2.0) * t1);
                }
                
                rd = rotateZ(rd, angle * PI / 2.0);
                
                ro.xy += level1_size * p;
                
                ro += rd * 0.2;
                rd = normalize(rd);
                
                float3 col = rain(ro, rd, time);
                
                return fixed4(col, 1.0);
            }
            // fixed4 frag(v2f _iParam) : SV_Target {
            //     fixed4 col;
            //     mainImage(col, gl_FragCoord);
            //     return col;
            // }

            ENDCG
        }
    }
}
