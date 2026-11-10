#include "hardware_smoke_output.h"
#include <initializer_list>
#include <memory>
#include <stdexcept>
#include <string>

static void require(bool ok,const char* message){if(!ok)throw std::runtime_error(message);}
static rks_tensor tensor(std::initializer_list<uint32_t> dims,int32_t layout=0){
    rks_tensor result{};result.n_dims=static_cast<uint32_t>(dims.size());result.layout=layout;
    result.element_count=1;unsigned i=0;
    for(uint32_t dim:dims){result.dims[i++]=dim;result.element_count*=dim;}
    return result;
}
struct Result { bool valid;std::string text; };
static Result inspect(const rks_tensor& attr,const std::vector<float>& data){
    std::unique_ptr<std::FILE,decltype(&std::fclose)> stream(std::tmpfile(),std::fclose);
    require(bool(stream),"tmpfile failed");
    bool valid=rks_test::print_float_output(stream.get(),7,attr,data,0);
    require(std::fflush(stream.get())==0,"flush failed");std::rewind(stream.get());
    std::string text;char buffer[256];
    while(size_t length=std::fread(buffer,1,sizeof(buffer),stream.get()))text.append(buffer,length);
    require(!std::ferror(stream.get()),"read failed");return {valid,text};
}
static unsigned channel_lines(const std::string& text){
    unsigned count=0;size_t position=0;
    while((position=text.find("\n channel",position))!=std::string::npos){
        position+=9;if(position<text.size()&&text[position]>='0'&&text[position]<='9')count++;
    }
    return count;
}
int main(){try{
    unsigned cases=0;
    auto reject=[&](const rks_tensor& attr,const std::vector<float>& data){
        auto result=inspect(attr,data);require(!result.valid,"invalid tensor/buffer accepted");
        require(result.text.find("range=[")==std::string::npos,"invalid descriptor reached range traversal");cases++;
    };
    reject(tensor({1,1,80,80}),{});
    reject(tensor({1,1,80,80}),std::vector<float>(6399,1.f));
    reject(tensor({1,1,1,1}),{});
    reject(tensor({1,1,1,1}),{1.f,2.f});
    auto score_sum=tensor({1,1,80,80});score_sum.data_type=2;score_sum.size_bytes=6400;
    std::vector<float> scores(6400,.25f);scores.front()=-2.f;scores.back()=3.f;
    auto single=inspect(score_sum,scores);
    require(single.valid&&single.text.find("range=[-2,3] negative=1 nonfinite=0")!=std::string::npos,
            "INT8 metadata float view lost global values");
    require(channel_lines(single.text)==1&&single.text.find("channel0 [-2,3]")!=std::string::npos,
            "score_sum must inspect exactly one complete channel");cases++;
    {auto result=inspect(tensor({1,1,1,1}),{.125f});
     require(result.valid&&channel_lines(result.text)==1&&result.text.find("channel0 [0.125,0.125]")!=std::string::npos,
             "one-element channel boundary failed");cases++;}
    for(unsigned channels:{1u,2u,5u,6u,7u,64u}){
        auto attr=tensor({2,channels,2,2});std::vector<float> data(attr.element_count);
        for(unsigned c=0;c<channels;c++)for(unsigned j=0;j<4;j++)data[c*4+j]=float(c*10+j);
        std::fill(data.begin()+channels*4,data.end(),-100.f);
        auto result=inspect(attr,data);require(result.valid,"valid multi-channel tensor rejected");
        require(channel_lines(result.text)==std::min(6u,channels),"channel preview count mismatch");
        for(unsigned c=0;c<std::min(6u,channels);c++){
            auto expected="channel"+std::to_string(c)+" ["+std::to_string(c*10)+","+std::to_string(c*10+3)+"]";
            require(result.text.find(expected)!=std::string::npos,"NCHW channel range mixed channels/batches");
        }
        require(result.text.find("range=[-100,")!=std::string::npos,"global range missed second batch");cases++;
    }
    for(auto attr:{tensor({6}),tensor({2,3}),tensor({1,2,3}),tensor({1,2,3,1},1),tensor({1,1,2,3},2)}){
        auto result=inspect(attr,{1.f,2.f,3.f,4.f,5.f,6.f});
        require(result.valid&&channel_lines(result.text)==0,"non-NCHW layout/rank interpreted as NCHW");
        require(result.text.find("range=[1,6]")!=std::string::npos,"generic flat statistics missing");cases++;
    }
    {auto attr=tensor({1});attr.n_dims=16;std::fill(attr.dims,attr.dims+16,1u);
     require(inspect(attr,{42.f}).valid,"maximum valid rank rejected");cases++;}
    {auto attr=tensor({1});attr.n_dims=0;reject(attr,{1.f});}
    {auto attr=tensor({1});attr.n_dims=17;reject(attr,{1.f});}
    {auto attr=tensor({1,1,1,0});reject(attr,{1.f});}
    {auto attr=tensor({1,1,1,1});attr.element_count=0;reject(attr,{1.f});}
    {auto attr=tensor({1,1,2,2});attr.element_count=3;reject(attr,{1.f,2.f,3.f});}
    {auto attr=tensor({1});attr.n_dims=16;std::fill(attr.dims,attr.dims+16,UINT32_MAX);reject(attr,{1.f});}
    {auto attr=tensor({1});attr.n_dims=2;attr.dims[0]=UINT32_MAX;attr.dims[1]=UINT32_MAX;reject(attr,{1.f});}
    {auto attr=tensor({1});attr.n_dims=4;attr.dims[0]=attr.dims[1]=1;attr.dims[2]=attr.dims[3]=65536;reject(attr,{1.f});}
    for(float invalid:{std::numeric_limits<float>::quiet_NaN(),std::numeric_limits<float>::infinity(),
                       -std::numeric_limits<float>::infinity()}){
        auto result=inspect(tensor({1,1,1,2}),{0.f,invalid});
        require(!result.valid&&result.text.find("nonfinite=1")!=std::string::npos,"nonfinite output assertion lost");cases++;
    }
    std::printf("hardware_smoke_output_unit PASS: %u buffer/shape/layout/overflow/float-view/range/nonfinite cases\n",cases);
    return 0;
}catch(const std::exception& e){std::fprintf(stderr,"%s\n",e.what());return 1;}}
